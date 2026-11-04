namespace Fixia.Payments.Domain.Exceptions;

/// <summary>
/// Quien pide participa en el pago pero no tiene permiso para esa operación, por ejemplo el prestador que intenta
/// procesar el cobro (GC-477). Se responde 403.
/// </summary>
public class PaymentAccessDeniedException : PaymentDomainException
{
    public PaymentAccessDeniedException(string message) : base(message)
    {
    }
}
