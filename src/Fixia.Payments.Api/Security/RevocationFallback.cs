using System.Collections.Concurrent;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Fixia.Payments.Api.Security;

/// <summary>Configuración de la revocación por lista local (sección <c>SessionRevocations</c>, GC-547).</summary>
public sealed class SessionRevocationOptions
{
    public const string SectionName = "SessionRevocations";

    public bool ConsumerEnabled { get; set; } = true;
    public string Topic { get; set; } = "fixia.users.sessions";
    public string GroupPrefix { get; set; } = "ms-payments-revocations";

    /// <summary>Cuánto se relee al arrancar: más que la vida del access token (15 min).</summary>
    public int LookbackMinutes { get; set; } = 20;

    /// <summary>Tiempo sin mensajes tras el cual se comprueba con el broker que la lista está al día.</summary>
    public int IdleIntervalMilliseconds { get; set; } = 1000;

    /// <summary>Antigüedad máxima de la última confirmación para usar la lista; si se supera, se falla cerrado.</summary>
    public int MaxStalenessSeconds { get; set; } = 5;
}

/// <summary>
/// Lista local de access tokens revocados, alimentada por <c>sesion.revocada</c> de ms-users (GC-547). Es la única fuente
/// de la revocación: solo vale si el consumidor confirmó hace poco que está al día. Vive en memoria; cada instancia la
/// reconstruye al arrancar y olvida cada <c>jti</c> cuando vence.
/// </summary>
public sealed class RevocationList
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxStaleness;
    private long _caughtUpTicks; // 0 = nunca

    public RevocationList(TimeProvider time, IOptions<SessionRevocationOptions> options)
    {
        _time = time;
        _maxStaleness = TimeSpan.FromSeconds(options.Value.MaxStalenessSeconds);
    }

    public void Add(string jti, DateTimeOffset expiresAt)
    {
        var now = _time.GetUtcNow();
        if (expiresAt > now)
            _revoked[jti] = expiresAt;
        foreach (var entry in _revoked)
        {
            if (entry.Value <= now)
                _revoked.TryRemove(entry.Key, out _);
        }
    }

    public bool Contains(string jti) =>
        _revoked.TryGetValue(jti, out var expiresAt) && expiresAt > _time.GetUtcNow();

    /// <summary>El consumidor confirmó con el broker que no le falta nada por leer.</summary>
    public void MarkCaughtUp() => Interlocked.Exchange(ref _caughtUpTicks, _time.GetUtcNow().UtcTicks);

    /// <summary>¿Se puede decidir con la lista? Solo si la última confirmación es reciente.</summary>
    public bool IsFresh
    {
        get
        {
            var ticks = Interlocked.Read(ref _caughtUpTicks);
            return ticks != 0 && new DateTimeOffset(ticks, TimeSpan.Zero) + _maxStaleness >= _time.GetUtcNow();
        }
    }
}

/// <summary>
/// Revocación sin consultar a ms-users (GC-547, cero dependencias síncronas): decide la lista local que alimenta
/// <c>sesion.revocada</c>. Un token revocado puede servir aquí el tiempo que tarda el evento en llegar (el retraso del
/// Outbox de ms-users, ~2 s, más Kafka); el PO aceptó una ventana corta. Si la lista no está al día, lanza
/// <see cref="TokenRevocationCheckException"/> y la petición falla cerrada (401).
/// </summary>
public sealed class ReplicatedTokenRevocationChecker : ITokenRevocationChecker
{
    private readonly RevocationList _list;

    public ReplicatedTokenRevocationChecker(RevocationList list)
    {
        _list = list;
    }

    public Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default)
    {
        if (_list.Contains(jti))
            return Task.FromResult(true);
        if (!_list.IsFresh)
            throw new TokenRevocationCheckException("La lista local de revocados no está al día");
        return Task.FromResult(false);
    }
}

/// <summary>
/// Consume <c>fixia.users.sessions</c> y llena la <see cref="RevocationList"/>. Cada instancia lee todo (grupo único,
/// sin confirmar offsets) y al arrancar retrocede <c>LookbackMinutes</c>. Tras cada intervalo sin mensajes pregunta al
/// broker el último offset de cada partición: solo si la posición lo alcanzó marca la lista como al día. Si el broker no
/// responde no se marca y la lista deja de usarse en pocos segundos.
/// </summary>
public sealed class SessionRevocationConsumer : BackgroundService
{
    private static readonly TimeSpan BrokerTimeout = TimeSpan.FromMilliseconds(500);

    private readonly RevocationList _list;
    private readonly SessionRevocationOptions _options;
    private readonly TimeProvider _time;
    private readonly string _bootstrapServers;
    private readonly ILogger<SessionRevocationConsumer> _logger;

    public SessionRevocationConsumer(RevocationList list, IOptions<SessionRevocationOptions> options,
        TimeProvider time, IConfiguration configuration, ILogger<SessionRevocationConsumer> logger)
    {
        _list = list;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _bootstrapServers = configuration["Kafka:BootstrapServers"]
                            ?? configuration["SPRING_KAFKA_BOOTSTRAP_SERVERS"]
                            ?? "localhost:9092";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // Consume() bloquea: que no frene el arranque del servicio.

        var config = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = $"{_options.GroupPrefix}-{Guid.NewGuid()}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Latest,
            AllowAutoCreateTopics = true
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => _logger.LogWarning("Kafka ({Servers}): {Reason}", _bootstrapServers, error.Reason))
            .SetPartitionsAssignedHandler((c, partitions) => StartOffsets(c, partitions))
            .Build();
        consumer.Subscribe(_options.Topic);
        var idle = TimeSpan.FromMilliseconds(_options.IdleIntervalMilliseconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(idle);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning("No se pudo leer de '{Topic}': {Reason}", _options.Topic, ex.Error.Reason);
                    await Task.Delay(idle, stoppingToken);
                    continue;
                }

                if (result?.Message is null)
                    CheckCaughtUp(consumer);
                else
                    Apply(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // apagado normal
        }
        finally
        {
            consumer.Close();
        }
    }

    private IEnumerable<TopicPartitionOffset> StartOffsets(IConsumer<string, string> consumer,
        List<TopicPartition> partitions)
    {
        var since = new Timestamp(_time.GetUtcNow().AddMinutes(-_options.LookbackMinutes).UtcDateTime);
        try
        {
            return consumer.OffsetsForTimes(partitions.Select(p => new TopicPartitionTimestamp(p, since)), BrokerTimeout * 10)
                .Select(found => found.Offset == Offset.End || found.Offset.Value < 0
                    ? new TopicPartitionOffset(found.TopicPartition, Offset.End)
                    : found);
        }
        catch (KafkaException)
        {
            // Sin respuesta del broker: se relee todo lo retenido, que también cubre los últimos minutos.
            return partitions.Select(p => new TopicPartitionOffset(p, Offset.Beginning));
        }
    }

    private void Apply(ConsumeResult<string, string> result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Message.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("eventType", out var type) || type.GetString() != "sesion.revocada")
                return;
            var data = root.GetProperty("data");
            var jti = data.GetProperty("jti").GetString();
            var expiresAt = data.GetProperty("expiresAt").GetDateTimeOffset();
            if (string.IsNullOrWhiteSpace(jti))
                throw new FormatException("Falta data.jti");
            _list.Add(jti, expiresAt);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException
                                       or InvalidOperationException)
        {
            // Solo dónde estaba el mensaje: nunca su contenido.
            _logger.LogError("Mensaje inválido en {TopicPartitionOffset}, se salta ({Error})",
                result.TopicPartitionOffset, ex.GetType().Name);
        }
    }

    private void CheckCaughtUp(IConsumer<string, string> consumer)
    {
        var partitions = consumer.Assignment;
        if (partitions.Count == 0)
            return;
        try
        {
            foreach (var partition in partitions)
            {
                var high = consumer.QueryWatermarkOffsets(partition, BrokerTimeout).High;
                var position = consumer.Position(partition);
                if (position == Offset.Unset ? high.Value != 0 : position.Value < high.Value)
                    return;
            }

            _list.MarkCaughtUp();
        }
        catch (KafkaException ex)
        {
            // El broker no respondió: no se marca y la lista envejece hasta dejar de usarse.
            _logger.LogDebug("No se pudo confirmar que la lista de revocados está al día ({Error})", ex.GetType().Name);
        }
    }
}
