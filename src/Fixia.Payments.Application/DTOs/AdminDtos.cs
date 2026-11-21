using System.Text.Json.Nodes;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Application.DTOs;

/// <summary>
/// Un pago en la lista del ADMIN (GC-535, contrato admin §4.1): el <see cref="PaymentResponse"/> de siempre más si hoy
/// procede un reembolso y de qué tipo sería.
/// </summary>
public sealed record AdminPaymentResponse : PaymentResponse
{
    public bool Refundable { get; init; }

    /// <summary><c>RELEASE</c>, <c>REVERSAL</c>, <c>GATEWAY</c> o <c>null</c> si no procede.</summary>
    public string? RefundKind { get; init; }

    public AdminPaymentResponse(PaymentResponse source, string? refundKind) : base(source)
    {
        RefundKind = refundKind;
        Refundable = refundKind is not null;
    }
}

public sealed record AdminPaymentPage(IReadOnlyList<AdminPaymentResponse> Items, string? NextCursor);

/// <summary>Una acción registrada del ADMIN (contrato admin §4.4 y §5). <c>Details</c> es el JSON guardado.</summary>
public sealed record AdminAuditEventResponse(
    Guid Id,
    Guid AdminId,
    AdminAction Action,
    string? TargetType,
    Guid? TargetId,
    JsonNode? Details,
    AdminAuditOutcome Outcome,
    DateTime OccurredAt)
{
    public static AdminAuditEventResponse From(AdminAuditEvent e) => new(e.Id, e.AdminId, e.Action, e.TargetType,
        e.TargetId, e.Details is null ? null : JsonNode.Parse(e.Details), e.Outcome, e.OccurredAt);
}

public sealed record AdminAuditPage(IReadOnlyList<AdminAuditEventResponse> Items, string? NextCursor);
