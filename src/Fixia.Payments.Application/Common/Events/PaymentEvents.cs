using System.Globalization;
using Fixia.Payments.Domain.Entities;

namespace Fixia.Payments.Application.Common.Events;

/// <summary>
/// Eventos que ms-payments ya emitía antes de GC-477, ahora escritos en el Outbox (versión 1). El contrato está en
/// <c>docs/events/README.md</c>. Los importes viajan como texto con dos decimales, igual que en ms-services, para que
/// ningún consumidor los convierta a punto flotante.
/// </summary>
public static class PaymentEvents
{
    public const string Topic = "fixia.payments.events";
    public const string Created = "pago.creado";
    public const string Completed = "pago.completado";
    public const string Failed = "pago.fallido";
    public const string Refunded = "pago.reembolsado";
    public const short SchemaVersion = 1;

    public static BusinessEvent PaymentCreated(Payment payment, EventActor actor) =>
        Build(Created, payment, actor, new
        {
            paymentId = payment.Id,
            serviceRequestId = payment.ServiceRequestId,
            clientId = payment.ClientId,
            providerId = payment.ProviderId,
            amount = Money(payment),
            method = payment.Method.ToString(),
            status = payment.Status.ToString()
        });

    public static BusinessEvent PaymentCompleted(Payment payment, EventActor actor) =>
        Build(Completed, payment, actor, new
        {
            paymentId = payment.Id,
            serviceRequestId = payment.ServiceRequestId,
            clientId = payment.ClientId,
            providerId = payment.ProviderId,
            amount = Money(payment),
            transactionReference = payment.TransactionReference,
            status = payment.Status.ToString()
        });

    public static BusinessEvent PaymentFailed(Payment payment, EventActor actor) =>
        Build(Failed, payment, actor, new
        {
            paymentId = payment.Id,
            serviceRequestId = payment.ServiceRequestId,
            clientId = payment.ClientId,
            providerId = payment.ProviderId,
            amount = Money(payment),
            failureReason = payment.FailureReason,
            status = payment.Status.ToString()
        });

    public static BusinessEvent PaymentRefunded(Payment payment, EventActor actor, string refundReference, string? reason) =>
        Build(Refunded, payment, actor, new
        {
            paymentId = payment.Id,
            serviceRequestId = payment.ServiceRequestId,
            amount = Money(payment),
            refundReference,
            reason,
            status = payment.Status.ToString()
        });

    private static BusinessEvent Build(string type, Payment payment, EventActor actor, object data) =>
        new(type, SchemaVersion, payment.Id, payment.ServiceRequestId,
            payment.UpdatedAt ?? payment.CreatedAt, actor, data);

    private static object Money(Payment payment) => new
    {
        amount = payment.Amount.ToString("0.00", CultureInfo.InvariantCulture),
        currency = payment.Currency
    };
}
