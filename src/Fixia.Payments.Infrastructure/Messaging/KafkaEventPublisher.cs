using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Infrastructure.Messaging;

/// <summary>
/// Productor de Kafka que usa el relay del Outbox (GC-477). Antes publicaba directo desde el servicio y, si Kafka
/// fallaba, el evento se perdía ("modo degradado"); ahora la falla se propaga para que el Outbox reintente.
/// Productor idempotente con <c>acks=all</c>, como pide el contrato de eventos.
/// </summary>
public sealed class KafkaEventPublisher : IMessagePublisher, IDisposable
{
    private readonly ILogger<KafkaEventPublisher> _logger;
    private readonly Lazy<IProducer<string, string>> _producer;

    public KafkaEventPublisher(IConfiguration configuration, ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;
        var bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? configuration["SPRING_KAFKA_BOOTSTRAP_SERVERS"]
            ?? "localhost:9092";

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000,
            SocketTimeoutMs = 5000
        };

        // Se crea al primer uso: el servicio arranca aunque Kafka todavía no esté disponible.
        _producer = new Lazy<IProducer<string, string>>(() =>
        {
            _logger.LogInformation("Productor Kafka inicializado apuntando a '{Servers}'.", bootstrapServers);
            return new ProducerBuilder<string, string>(config).Build();
        });
    }

    public async Task PublishAsync(BrokerMessage message, CancellationToken ct = default)
    {
        var headers = new Headers();
        foreach (var (name, value) in message.Headers)
            headers.Add(name, Encoding.UTF8.GetBytes(value));

        var result = await _producer.Value.ProduceAsync(message.Topic, new Message<string, string>
        {
            Key = message.Key,
            Value = message.Value,
            Headers = headers
        }, ct);

        // Nunca se registra el contenido del evento, solo su ubicación.
        _logger.LogInformation("Evento publicado en '{Topic}' [Partición: {Partition}, Offset: {Offset}].",
            message.Topic, result.Partition.Value, result.Offset.Value);
    }

    public void Dispose()
    {
        if (!_producer.IsValueCreated)
            return;
        _producer.Value.Flush(TimeSpan.FromSeconds(5));
        _producer.Value.Dispose();
    }
}
