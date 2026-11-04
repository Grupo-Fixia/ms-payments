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
            _logger.LogWarning(ex, "No se pudo aplicar el mensaje {TopicPartitionOffset}; se reintenta.",
                result.TopicPartitionOffset);
            consumer.Seek(result.TopicPartitionOffset);
            await Task.Delay(_options.RetryDelayMilliseconds, ct);
        }
    }
}
