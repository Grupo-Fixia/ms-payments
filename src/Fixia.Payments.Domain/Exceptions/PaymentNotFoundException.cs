namespace Fixia.Payments.Domain.Exceptions;

public class PaymentNotFoundException : PaymentDomainException
{
    public Guid PaymentId { get; }

    public PaymentNotFoundException(Guid paymentId)
        : base($"El pago con ID '{paymentId}' no fue encontrado.")
    {
        PaymentId = paymentId;
    }

    private PaymentNotFoundException(string message) : base(message)
    {
    }

    /// <summary>
    /// No hay pago para la solicitud, o quien pregunta no participa en él: la respuesta es la misma en los dos casos
    /// para no revelar pagos ajenos (GC-477).
    /// </summary>
    public static PaymentNotFoundException ForServiceRequest(Guid serviceRequestId) =>
        new($"No existe un pago asociado a la solicitud de servicio '{serviceRequestId}'.");
}
