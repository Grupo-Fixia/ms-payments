namespace Fixia.Payments.Domain.Exceptions;

public class PaymentNotFoundException : PaymentDomainException
{
    public Guid PaymentId { get; }

    public PaymentNotFoundException(Guid paymentId)
        : base($"El pago con ID '{paymentId}' no fue encontrado.")
    {
        PaymentId = paymentId;
    }
}

