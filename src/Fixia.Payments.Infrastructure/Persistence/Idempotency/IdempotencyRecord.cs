namespace Fixia.Payments.Infrastructure.Persistence.Idempotency;

/// <summary>
/// Una <c>Idempotency-Key</c> usada (GC-482, GC-483): quién, para qué operación, el hash del cuerpo y la respuesta
/// guardada. Se inserta al empezar la operación, dentro de su transacción, y se completa al final: si la operación
/// falla, la fila desaparece con el rollback y la clave se puede reintentar.
/// </summary>
public class IdempotencyRecord
{
    public Guid UserId { get; private set; }
    public string Operation { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;

    /// <summary>SHA-256 del cuerpo normalizado: misma clave con otro cuerpo es un error (422).</summary>
    public string RequestHash { get; private set; } = string.Empty;

    /// <summary>La respuesta en JSON; null mientras la operación no termina.</summary>
    public string? ResponseJson { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    // Constructor para EF Core
    private IdempotencyRecord() { }

    public static IdempotencyRecord Start(Guid userId, string operation, string key, string requestHash, DateTime now) =>
        new()
        {
            UserId = userId,
            Operation = operation,
            Key = key,
            RequestHash = requestHash,
            CreatedAt = now
        };

    public void Complete(string responseJson, DateTime now)
    {
        ResponseJson = responseJson;
        CompletedAt = now;
    }
}
