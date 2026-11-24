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
    /// Intentos de un mismo mensaje antes de apartarlo y seguir con el siguiente. Sin este límite, un mensaje que siempre
    /// falla deja la partición (y con un solo hilo, todo el consumidor) detenido: no se crean ni se cobran pagos.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>
/// Consume <c>fixia.services.service-requests</c> (GC-484, GC-485) con confirmación manual: el offset se confirma
/// solo después de guardar el efecto (al menos una vez). Si el efecto no se pudo guardar, vuelve al mismo mensaje y lo
/// reintenta; los repetidos no tienen efecto gracias a <c>processed_events</c>. Un mensaje que no cumple el contrato
/// se registra (sin su contenido) y se salta, porque reintentarlo no lo arregla.
/// </summary>
public sealed class ServiceEventsConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceEventsOptions _options;
    private readonly string _bootstrapServers;
    private readonly ILogger<ServiceEventsConsumer> _logger;
    private TopicPartitionOffset? _failingOffset;
    private int _failedAttempts;

    public ServiceEventsConsumer(IServiceScopeFactory scopeFactory, IConfiguration configuration,
        IOptions<ServiceEventsOptions> options, ILogger<ServiceEventsConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
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

    /// <summary>Cuenta un fallo del mensaje; devuelve <c>false</c> cuando ya no quedan intentos.</summary>
    private bool RegisterFailure(TopicPartitionOffset offset)
    {
        if (_failingOffset is null || !_failingOffset.Equals(offset))
        {
            _failingOffset = offset;
            _failedAttempts = 0;
        }

        _failedAttempts++;
        return _failedAttempts < Math.Max(1, _options.MaxAttempts);
    }

    /// <summary>
    /// Base caída o sin conexión: se reintenta sin límite, porque apartar el mensaje perdería el pago. Lo demás (un dato
    /// que falta, una regla del dominio) no se arregla reintentando.
    /// </summary>
    private static bool IsTransient(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is Npgsql.NpgsqlException { IsTransient: true } or TimeoutException)
                return true;
        }

        return false;
    }

    private void ResetFailures()
    {
        _failingOffset = null;
        _failedAttempts = 0;
    }

    private async Task ProcessAsync(IConsumer<string, string> consumer, ConsumeResult<string, string> result,
        CancellationToken ct)
    {
        try
        {
            var serviceEvent = ServiceEvent.Parse(result.Message.Value);
            using (var scope = _scopeFactory.CreateScope())
            {
                var handler = scope.ServiceProvider.GetRequiredService<IServiceEventsHandler>();
                await handler.HandleAsync(serviceEvent, ct);
            }

            consumer.Commit(result);
        }
        catch (InvalidServiceEventException ex)
        {
            // No se registra el contenido del mensaje, solo dónde estaba.
            _logger.LogError("Mensaje inválido en {TopicPartitionOffset}, se salta: {Reason}",
                result.TopicPartitionOffset, ex.Message);
            consumer.Commit(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!IsTransient(ex) && !RegisterFailure(result.TopicPartitionOffset))
            {
                // Se aparta para no detener los pagos de todos los demás servicios. El offset queda en el log para
                // reprocesarlo a mano una vez corregida la causa (processed_events evita aplicarlo dos veces).
                _logger.LogError(ex,
                    "El mensaje {TopicPartitionOffset} falló {Attempts} veces; se aparta y se continúa con el siguiente.",
                    result.TopicPartitionOffset, _options.MaxAttempts);
                consumer.Commit(result);
                ResetFailures();
                return;
            }

            _logger.LogWarning(ex, "No se pudo aplicar el mensaje {TopicPartitionOffset} (intento {Attempt} de {Max}); se reintenta.",
                result.TopicPartitionOffset, _failedAttempts, _options.MaxAttempts);
            consumer.Seek(result.TopicPartitionOffset);
            await Task.Delay(_options.RetryDelayMilliseconds, ct);
            return;
        }

        ResetFailures();
    }
}
