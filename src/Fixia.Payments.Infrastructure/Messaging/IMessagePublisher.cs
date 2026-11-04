namespace Fixia.Payments.Infrastructure.Messaging;

/// <summary>Un mensaje listo para Kafka: tópico, clave, cuerpo y cabeceras.</summary>
public sealed record BrokerMessage(string Topic, string Key, string Value, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Publica un mensaje y solo termina cuando el broker lo confirmó. Si no se pudo publicar, lanza la excepción: el
/// relay del Outbox la registra como intento fallido y reintenta (GC-477).
/// </summary>
public interface IMessagePublisher
{
    Task PublishAsync(BrokerMessage message, CancellationToken ct = default);
}
