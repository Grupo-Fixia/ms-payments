namespace Fixia.Payments.Domain.Exceptions;

/// <summary>La <c>Idempotency-Key</c> ya se usó con otro cuerpo (GC-482, GC-483). Se responde 422.</summary>
public class IdempotencyKeyReuseException : PaymentDomainException
{
    public IdempotencyKeyReuseException()
        : base("La Idempotency-Key ya se usó con otros datos. Use una clave nueva para una operación distinta.")
    {
    }
}
