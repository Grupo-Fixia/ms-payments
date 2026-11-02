using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Domain.Exceptions;

public class InvalidPaymentStatusException : PaymentDomainException
{
    public PaymentStatus CurrentStatus { get; }
    public string Operation { get; }

    public InvalidPaymentStatusException(PaymentStatus currentStatus, string operation)
        : base($"No se puede ejecutar la operación '{operation}' sobre un pago en estado '{currentStatus}'.")
    {
        CurrentStatus = currentStatus;
        Operation = operation;
    }
}

