namespace Fixia.Payments.Domain.Exceptions;

/// <summary>
/// El movimiento dejaría un saldo negativo (disponible o retenido). No se aplica nada (GC-480). Se responde 409.
/// </summary>
public class InsufficientBalanceException : PaymentDomainException
{
    public InsufficientBalanceException(string message) : base(message)
    {
    }
}
