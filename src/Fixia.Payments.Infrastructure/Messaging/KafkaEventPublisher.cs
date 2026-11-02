using System.Text.Json;
using Confluent.Kafka;
using Fixia.Payments.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Infrastructure.Messaging;

public class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly ILogger<KafkaEventPublisher> _logger;
    private readonly IProducer<string, string>? _producer;

    public KafkaEventPublisher(IConfiguration configuration, ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;
        var bootstrapServers = configuration["Kafka:BootstrapServers"] 
            ?? configuration["SPRING_KAFKA_BOOTSTRAP_SERVERS"] 
            ?? "localhost:9092";

        try
        {
            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                Acks = Acks.All,
                MessageSendMaxRetries = 3,
                RetryBackoffMs = 1000,
                SocketTimeoutMs = 5000
            };

            _producer = new ProducerBuilder<string, string>(config).Build();
            _logger.LogInformation("KafkaEventPublisher inicializado apuntando a '{Servers}'.", bootstrapServers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No fue posible conectar con el broker Kafka ({Servers}). Los eventos se registrarán únicamente en logs.", bootstrapServers);
        }
    }

    public async Task PublishAsync<T>(string topic, string eventType, T payload, CancellationToken ct = default)
    {
        var messageEnvelope = new
        {
            eventId = Guid.NewGuid().ToString(),
            eventType = eventType,
            timestamp = DateTime.UtcNow.ToString("o"),
            data = payload
        };

        var json = JsonSerializer.Serialize(messageEnvelope);

        if (_producer != null)
        {
            try
            {
                var deliveryResult = await _producer.ProduceAsync(topic, new Message<string, string>
                {
                    Key = eventType,
                    Value = json
                }, ct);

                _logger.LogInformation("Evento Kafka publicado a '{Topic}' [Partición: {Partition}, Offset: {Offset}]: {EventType}",
                    topic, deliveryResult.Partition.Value, deliveryResult.Offset.Value, eventType);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallo al enviar mensaje a Kafka en el tópico '{Topic}'. Se continúa en modo degradado.", topic);
            }
        }

        _logger.LogInformation("[KAFKA_FALLBACK] Mensaje no publicado en broker ({Topic}): {Message}", topic, json);
    }

    public void Dispose()
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
    }
}

