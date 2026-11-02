namespace Fixia.Payments.Domain.Exceptions;

public class PaymentDomainException : Exception
{
    public PaymentDomainException(string message) : base(message)
    {
    }

    public PaymentDomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

