using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Domain.Entities;

/// <summary>
/// Una acción del ADMIN de Fixia sobre pagos (GC-535, contrato <c>ms-users/docs/contracts/admin.md</c> §5): quién, qué,
/// sobre qué pago y con qué resultado. Es inmutable y ninguna ruta la edita ni la borra. <c>Details</c> es un JSON
/// pequeño (filtros de una lista; motivo, tipo e <c>Idempotency-Key</c> de un reembolso), nunca tokens ni secretos.
/// </summary>
public class AdminAuditEvent
{
    public const string TargetPayment = "PAYMENT";

    public Guid Id { get; private set; }
    public Guid AdminId { get; private set; }
    public AdminAction Action { get; private set; }
    public string? TargetType { get; private set; }
    public Guid? TargetId { get; private set; }
    public string? Details { get; private set; }
    public AdminAuditOutcome Outcome { get; private set; }
    public DateTime OccurredAt { get; private set; }

    // Constructor para EF Core
    private AdminAuditEvent() { }

    public AdminAuditEvent(Guid adminId, AdminAction action, Guid? targetPaymentId, string? details,
        AdminAuditOutcome outcome, DateTime occurredAt)
    {
        Id = Guid.NewGuid();
        AdminId = adminId;
        Action = action;
        TargetType = targetPaymentId is null ? null : TargetPayment;
        TargetId = targetPaymentId;
        Details = details;
        Outcome = outcome;
        // Microsegundos, como PostgreSQL: el cursor de la lista no se corre ni repite filas.
        OccurredAt = new DateTime(occurredAt.Ticks - occurredAt.Ticks % 10, DateTimeKind.Utc);
    }
}
