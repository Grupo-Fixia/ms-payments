namespace Fixia.Payments.Domain.Exceptions;

/// <summary>
/// La operación choca con un pago que ya existe o que cambió al mismo tiempo (GC-477). Se responde 409.
/// </summary>
public class PaymentConflictException : PaymentDomainException
{
    public PaymentConflictException(string message) : base(message)
    {
    }
}
