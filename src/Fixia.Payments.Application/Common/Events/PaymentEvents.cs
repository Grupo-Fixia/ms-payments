using System.Globalization;
using Fixia.Payments.Domain.Entities;

namespace Fixia.Payments.Application.Common.Events;

/// <summary>
/// Catálogo de eventos <c>pago.*</c> (versión 1), escritos en el Outbox. El contrato y los esquemas JSON están en
/// <c>docs/events</c> (GC-488). Los importes viajan como texto con dos decimales, igual que en ms-services, para que
/// ningún consumidor los convierta a punto flotante.
/// <para>
/// Los pagos con billetera (GC-480) agregan campos <b>opcionales</b> (<c>contractId</c> y <c>breakdown</c>): es un
/// cambio compatible, así que siguen en la versión 1.
/// </para>
/// </summary>
public static class PaymentEvents
{
    public const string Topic = "fixia.payments.events";
    public const string Created = "pago.creado";
    public const string Held = "pago.procesado";
    public const string Completed = "pago.completado";
    public const string Failed = "pago.fallido";
    public const string Refunded = "pago.reembolsado";
    public const short SchemaVersion = 1;

    /// <summary>Cómo se devolvió el dinero en <c>pago.reembolsado</c>.</summary>
    public static class RefundKinds
    {
        /// <summary>Se liberó lo retenido de un pago con billetera que no se había cobrado.</summary>
        public const string Release = "RELEASE";

        /// <summary>Se revirtió un pago con billetera ya cobrado: prestador y Fixia devuelven, el cliente recibe.</summary>
        public const string Reversal = "REVERSAL";

        /// <summary>Reembolso por la pasarela de un pago que no es de billetera.</summary>
        public const string Gateway = "GATEWAY";
    }

    public static BusinessEvent PaymentCreated(Payment payment, EventActor actor) =>
        Build(Created, payment, actor, Participants(payment)
            .With("amount", Money(payment.Amount, payment.Currency))
            .With("method", payment.Method.ToString())
            .WithBreakdown(payment)
            .With("status", payment.Status.ToString()));

    /// <summary>
    /// <c>pago.procesado</c>: con billetera, el total (precio + comisión) quedó <b>retenido</b> en la billetera del
    /// cliente (GC-484). Lo emitirá la retención al contratar.
    /// </summary>
    public static BusinessEvent PaymentHeld(Payment payment, EventActor actor) =>
        Build(Held, payment, actor, Participants(payment)
            .With("amount", Money(payment.Amount, payment.Currency))
            .WithBreakdown(payment)
            .With("status", payment.Status.ToString()));

    public static BusinessEvent PaymentCompleted(Payment payment, EventActor actor) =>
        Build(Completed, payment, actor, Participants(payment)
            .With("amount", Money(payment.Amount, payment.Currency))
            .With("transactionReference", payment.TransactionReference)
            .WithBreakdown(payment)
            .With("status", payment.Status.ToString()));

    public static BusinessEvent PaymentFailed(Payment payment, EventActor actor) =>
        Build(Failed, payment, actor, Participants(payment)
            .With("amount", Money(payment.Amount, payment.Currency))
            .With("failureReason", payment.FailureReason)
            .WithBreakdown(payment)
            .With("status", payment.Status.ToString()));

    public static BusinessEvent PaymentRefunded(Payment payment, EventActor actor, string refundReference, string reason,
        string kind) =>
        Build(Refunded, payment, actor, new Dictionary<string, object?>
            {
                ["paymentId"] = payment.Id,
                ["serviceRequestId"] = payment.ServiceRequestId,
                ["amount"] = Money(payment.Amount, payment.Currency),
                ["refundReference"] = refundReference,
                ["reason"] = reason,
                ["kind"] = kind
            }
            .WithBreakdown(payment)
            .With("status", payment.Status.ToString()));

    private static Dictionary<string, object?> Participants(Payment payment)
    {
        var data = new Dictionary<string, object?>
        {
            ["paymentId"] = payment.Id,
            ["serviceRequestId"] = payment.ServiceRequestId
        };
        if (payment.ContractId.HasValue)
            data["contractId"] = payment.ContractId.Value;
        data["clientId"] = payment.ClientId;
        data["providerId"] = payment.ProviderId;
        return data;
    }

    private static Dictionary<string, object?> With(this Dictionary<string, object?> data, string key, object? value)
    {
        data[key] = value;
        return data;
    }

    /// <summary>Desglose del pago con billetera: precio del prestador, comisión de Fixia, tarifa y regla aplicada.</summary>
    private static Dictionary<string, object?> WithBreakdown(this Dictionary<string, object?> data, Payment payment)
    {
        if (!payment.IsWalletPayment)
            return data;

        data["breakdown"] = new Dictionary<string, object?>
        {
            ["price"] = Money(payment.BaseAmount!.Value, payment.Currency),
            ["commission"] = Money(payment.CommissionAmount ?? 0m, payment.Currency),
            ["rate"] = (payment.CommissionRate ?? 0m).ToString("0.0000", CultureInfo.InvariantCulture),
            ["ruleId"] = payment.CommissionRuleId
        };
        return data;
    }

    private static BusinessEvent Build(string type, Payment payment, EventActor actor, object data) =>
        new(type, SchemaVersion, payment.Id, payment.ServiceRequestId,
            payment.UpdatedAt ?? payment.CreatedAt, actor, data);

    private static Dictionary<string, object?> Money(decimal amount, string currency) => new()
    {
        ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
        ["currency"] = currency
    };
}
