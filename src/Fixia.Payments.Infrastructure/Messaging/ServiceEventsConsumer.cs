using Confluent.Kafka;
using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fixia.Payments.Infrastructure.Messaging;

/// <summary>Configuración del consumidor de eventos de ms-services (sección <c>ServiceEvents</c>).</summary>
public sealed class ServiceEventsOptions
{
    public const string SectionName = "ServiceEvents";

    public bool ConsumerEnabled { get; set; } = true;
    public string Topic { get; set; } = ServiceEvent.Topic;
    public string GroupId { get; set; } = "ms-payments";

    /// <summary>Espera antes de reintentar un mensaje cuyo efecto no se pudo guardar (por ejemplo, base caída).</summary>
    public int RetryDelayMilliseconds { get; set; } = 5000;

    /// <summary>
    /// Intentos para un mensaje que falla con un error que no es transitorio antes de apartarlo. Los errores
    /// transitorios se reintentan siempre.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Tópico donde se copian los mensajes apartados, con el error en las cabeceras. Vacío: solo el log.</summary>
    public string DeadLetterTopic { get; set; } = ServiceEvent.Topic + ".dlt";
}

/// <summary>
/// Consume <c>fixia.services.service-requests</c> (GC-484, GC-485) con confirmación manual: el offset se confirma
/// solo después de guardar el efecto (al menos una vez). Si el efecto no se pudo guardar, vuelve al mismo mensaje y lo
/// reintenta; los repetidos no tienen efecto gracias a <c>processed_events</c>. Un mensaje que no cumple el contrato
/// se registra (sin su contenido) y se salta, porque reintentarlo no lo arregla.
/// <para>
/// Un mensaje que falla siempre con un error que no es transitorio (ver <see cref="ConsumerFailurePolicy"/>) se aparta
/// tras <see cref="ServiceEventsOptions.MaxAttempts"/> intentos: se copia al tópico DLT, se registra como error con su
/// ubicación y se confirma, para que no bloquee al resto de la partición.
/// </para>
/// </summary>
public sealed class ServiceEventsConsumer : BackgroundService
{
    public const string HeaderOriginalTopic = "x-original-topic";
    public const string HeaderOriginalPartition = "x-original-partition";
    public const string HeaderOriginalOffset = "x-original-offset";
    public const string HeaderErrorType = "x-error-type";
    public const string HeaderErrorMessage = "x-error-message";
    private const int MaxHeaderErrorLength = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagePublisher _publisher;
    private readonly ServiceEventsOptions _options;
    private readonly ConsumerFailurePolicy _failures;
    private readonly string _bootstrapServers;
    private readonly ILogger<ServiceEventsConsumer> _logger;

    public ServiceEventsConsumer(IServiceScopeFactory scopeFactory, IMessagePublisher publisher,
        IConfiguration configuration, IOptions<ServiceEventsOptions> options, ILogger<ServiceEventsConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _options = options.Value;
        _failures = new ConsumerFailurePolicy(_options.MaxAttempts);
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
            GroupId = _options.GroupId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = true
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => _logger.LogWarning("Kafka ({Servers}): {Reason}", _bootstrapServers, error.Reason))
            .Build();
        consumer.Subscribe(_options.Topic);
        _logger.LogInformation("Consumidor de eventos de servicios suscrito a '{Topic}' (grupo {Group}).",
            _options.Topic, _options.GroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning("No se pudo leer de '{Topic}': {Reason}", _options.Topic, ex.Error.Reason);
                    await Task.Delay(_options.RetryDelayMilliseconds, stoppingToken);
                    continue;
                }

                if (result?.Message is null)
                    continue;

                await ProcessAsync(consumer, result, stoppingToken);
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

    private async Task ProcessAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> result,
        CancellationToken ct)
    {
        ServiceEvent? serviceEvent = null;
        try
        {
            // Un mensaje sin cuerpo (tombstone) no es un evento: antes hacía fallar Parse con ArgumentNullException,
            // que no se reconocía como mensaje inválido y bloqueaba la partición.
            serviceEvent = ServiceEvent.Parse(result.Message.Value
                                              ?? throw new InvalidServiceEventException("mensaje sin contenido"));
            using (var scope = _scopeFactory.CreateScope())
            {
                var handler = scope.ServiceProvider.GetRequiredService<IServiceEventsHandler>();
                await handler.HandleAsync(serviceEvent, ct);
            }

            consumer.Commit(result);
            _failures.OnDone(result.TopicPartitionOffset);
        }
        catch (InvalidServiceEventException ex)
        {
            // No se registra el contenido del mensaje, solo dónde estaba.
            _logger.LogError("Mensaje inválido en {TopicPartitionOffset}, se salta: {Reason}",
                result.TopicPartitionOffset, ex.Message);
            TryCommit(consumer, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (action, attempts) = _failures.OnFailure(result.TopicPartitionOffset, ex);
            if (action == ConsumerFailureAction.Park)
            {
                await ParkAsync(consumer, result, serviceEvent, ex, attempts, ct);
                return;
            }

            _logger.LogWarning(ex,
                "No se pudo aplicar el mensaje {TopicPartitionOffset} ({EventType} {EventId}); se reintenta (fallo {Attempts} de {Max} si no es transitorio).",
                result.TopicPartitionOffset, serviceEvent?.EventType, serviceEvent?.EventId, attempts,
                _failures.MaxAttempts);
            consumer.Seek(result.TopicPartitionOffset);
            await Task.Delay(_options.RetryDelayMilliseconds, ct);
        }
    }

    /// <summary>
    /// Aparta un mensaje que falla siempre: lo copia al DLT (si se puede), deja un error con su ubicación para
    /// reprocesarlo después y confirma el offset para seguir con la partición.
    /// </summary>
    private async Task ParkAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> result,
        ServiceEvent? serviceEvent, Exception error, int attempts, CancellationToken ct)
    {
        _logger.LogError(error,
            "El mensaje {TopicPartitionOffset} ({EventType} {EventId}) falló {Attempts} veces con un error que no es transitorio; se aparta en '{DeadLetterTopic}' para no bloquear la partición. Hay que revisarlo y reprocesarlo.",
            result.TopicPartitionOffset, serviceEvent?.EventType, serviceEvent?.EventId, attempts,
            _options.DeadLetterTopic);

        if (!string.IsNullOrWhiteSpace(_options.DeadLetterTopic))
        {
            try
            {
                await _publisher.PublishAsync(DeadLetter(result, error), ct);
            }
            catch (Exception publishError) when (publishError is not OperationCanceledException)
            {
                _logger.LogError(
                    "No se pudo copiar {TopicPartitionOffset} a '{DeadLetterTopic}' ({Error}); sigue en el tópico original en esa posición.",
                    result.TopicPartitionOffset, _options.DeadLetterTopic, publishError.Message);
            }
        }

        TryCommit(consumer, result);
    }

    private BrokerMessage DeadLetter(ConsumeResult<string, string> result, Exception error)
    {
        var message = $"{error.GetType().Name}: {error.Message}";
        return new BrokerMessage(_options.DeadLetterTopic!, result.Message.Key ?? string.Empty,
            result.Message.Value ?? string.Empty,
            new Dictionary<string, string>
            {
                [HeaderOriginalTopic] = result.Topic,
                [HeaderOriginalPartition] = result.Partition.Value.ToString(),
                [HeaderOriginalOffset] = result.Offset.Value.ToString(),
                [HeaderErrorType] = error.GetType().FullName ?? error.GetType().Name,
                [HeaderErrorMessage] = message.Length <= MaxHeaderErrorLength ? message : message[..MaxHeaderErrorLength]
            });
    }

    /// <summary>
    /// Confirma sin dejar caer el consumidor: si Kafka no responde, el mensaje vuelve a llegar y se trata otra vez
    /// (un evento ya aplicado se ignora por <c>processed_events</c>).
    /// </summary>
    private void TryCommit(IConsumer<string, string> consumer, ConsumeResult<string, string> result)
    {
        try
        {
            consumer.Commit(result);
            _failures.OnDone(result.TopicPartitionOffset);
        }
        catch (KafkaException ex)
        {
            _logger.LogWarning("No se pudo confirmar {TopicPartitionOffset}: {Reason}", result.TopicPartitionOffset,
                ex.Error.Reason);
        }
    }
}
