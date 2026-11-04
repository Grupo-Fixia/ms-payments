namespace Fixia.Payments.Infrastructure.Persistence.Outbox;

public enum OutboxStatus
{
    Pending,
    Published,
    Failed
}

/// <summary>
/// Evento de negocio en el Outbox transaccional (GC-477), con las mismas reglas que el de ms-services
/// (<c>docs/events/README.md</c> de ms-services, sección 10). Se escribe en la misma transacción que el cambio que lo
/// produjo, así que existe si y solo si ese cambio quedó confirmado.
/// <para>
/// Tiene dos partes: el <b>hecho</b> (identidad, tipo, agregado, secuencia y el sobre JSON completo), que no cambia
/// nunca, y el <b>envío</b> (estado, intentos, próximo intento, último error y fecha de publicación), que solo toca el
/// relay con <see cref="MarkPublished"/>, <see cref="RegisterFailedAttempt"/> y <see cref="Requeue"/>.
/// </para>
/// </summary>
public class OutboxEvent
{
    public const string AggregateTypePayment = "Payment";
    public const int MaxPayloadLength = 16000;
    public const int MaxErrorLength = 500;

    /// <summary>Tras este número de fallos el evento pasa a <see cref="OutboxStatus.Failed"/>.</summary>
    public const int MaxAttempts = 10;

    /// <summary>Espera máxima entre reintentos.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public short SchemaVersion { get; private set; }
    public string AggregateType { get; private set; } = string.Empty;
    public Guid AggregateId { get; private set; }
    public long AggregateSequence { get; private set; }
    public string Topic { get; private set; } = string.Empty;
    public string MessageKey { get; private set; } = string.Empty;

    /// <summary>El sobre completo en JSON, tal como se publicará.</summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTime OccurredAt { get; private set; }
    public OutboxStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTime NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? PublishedAt { get; private set; }

    // Constructor para EF Core
    private OutboxEvent() { }

    /// <summary>Un hecho nuevo, pendiente de publicar desde ya.</summary>
    /// <exception cref="ArgumentException">Si falta un dato, la secuencia o la versión no son positivas o el sobre no cabe.</exception>
    public static OutboxEvent Pending(Guid eventId, string eventType, short schemaVersion, string aggregateType,
        Guid aggregateId, long aggregateSequence, string topic, string messageKey, string payload, DateTime occurredAt)
    {
        Require(eventId != Guid.Empty, "El id del evento es obligatorio");
        Require(!string.IsNullOrWhiteSpace(eventType), "El tipo del evento es obligatorio");
        Require(schemaVersion >= 1, "La versión del esquema debe ser positiva");
        Require(!string.IsNullOrWhiteSpace(aggregateType), "El tipo del agregado es obligatorio");
        Require(aggregateId != Guid.Empty, "El agregado es obligatorio");
        Require(aggregateSequence >= 1, "La secuencia debe ser positiva");
        Require(!string.IsNullOrWhiteSpace(topic), "El tópico es obligatorio");
        Require(!string.IsNullOrWhiteSpace(messageKey), "La clave del mensaje es obligatoria");
        Require(!string.IsNullOrWhiteSpace(payload), "El contenido del evento es obligatorio");
        Require(payload.Length <= MaxPayloadLength, $"El evento supera {MaxPayloadLength} caracteres");
        Require(occurredAt.Kind == DateTimeKind.Utc, "El instante del hecho debe estar en UTC");

        return new OutboxEvent
        {
            EventId = eventId,
            EventType = eventType,
            SchemaVersion = schemaVersion,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            AggregateSequence = aggregateSequence,
            Topic = topic,
            MessageKey = messageKey,
            Payload = payload,
            OccurredAt = occurredAt,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            NextAttemptAt = occurredAt
        };
    }

    /// <summary>
    /// Kafka confirmó la publicación. Repetirlo sobre un evento ya publicado no cambia nada (reentrega).
    /// </summary>
    /// <exception cref="InvalidOperationException">Si el evento está fallido: primero hay que reactivarlo.</exception>
    public void MarkPublished(DateTime now)
    {
        if (Status == OutboxStatus.Published)
            return;
        if (Status == OutboxStatus.Failed)
            throw new InvalidOperationException("Un evento fallido se reactiva antes de publicarlo");

        Status = OutboxStatus.Published;
        PublishedAt = now;
        LastError = null;
    }

    /// <summary>
    /// Un intento de publicación falló: suma el intento y programa el siguiente con espera exponencial (2, 4, 8…
    /// segundos, máximo <see cref="MaxBackoff"/>). Al llegar a <see cref="MaxAttempts"/> queda fallido. El error se
    /// recorta a <see cref="MaxErrorLength"/> caracteres y nunca debe incluir el contenido del evento.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si el evento ya está publicado.</exception>
    public void RegisterFailedAttempt(string error, DateTime now)
    {
        if (Status == OutboxStatus.Published)
            throw new InvalidOperationException("El evento ya está publicado");

        Attempts++;
        LastError = error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
        if (Attempts >= MaxAttempts)
            Status = OutboxStatus.Failed;
        else
            NextAttemptAt = now + Backoff(Attempts);
    }

    /// <summary>Reactiva un evento fallido para que el relay lo vuelva a intentar desde ya, con los intentos en cero.</summary>
    /// <exception cref="InvalidOperationException">Si el evento no está fallido.</exception>
    public void Requeue(DateTime now)
    {
        if (Status != OutboxStatus.Failed)
            throw new InvalidOperationException("Solo se reactiva un evento fallido");

        Status = OutboxStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
    }

    /// <summary>Espera antes del intento siguiente: 2^intentos segundos (2, 4… 256) y, desde el noveno, el tope.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        if (attempts < 1)
            return TimeSpan.Zero;
        var seconds = Math.Pow(2, Math.Min(attempts, 30));
        var delay = TimeSpan.FromSeconds(seconds);
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new ArgumentException(message);
    }
}
