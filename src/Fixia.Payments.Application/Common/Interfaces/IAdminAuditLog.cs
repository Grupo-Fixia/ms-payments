using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Application.Common.Interfaces;

/// <summary>Registro de las acciones del ADMIN en ms-payments (GC-535, contrato admin §5).</summary>
public interface IAdminAuditLog
{
    /// <summary>
    /// Agrega el registro a la unidad de trabajo actual: se guarda con el próximo <c>SaveChanges</c>, en la misma
    /// transacción que la acción (el reembolso).
    /// </summary>
    void Stage(Guid adminId, AdminAction action, Guid? targetPaymentId, object? details, AdminAuditOutcome outcome);

    /// <summary>
    /// Guarda el registro ya, en su propia transacción. Para las consultas y para las acciones que fallaron, cuya
    /// transacción se revirtió y no debe arrastrar nada pendiente.
    /// </summary>
    Task RecordAsync(Guid adminId, AdminAction action, Guid? targetPaymentId, object? details,
        AdminAuditOutcome outcome, CancellationToken ct = default);

    /// <summary>Del más reciente al más antiguo, después de <paramref name="after"/>.</summary>
    Task<IReadOnlyList<AdminAuditEvent>> ListAsync(Guid? adminId, AdminAction? action, AdminListPosition? after,
        int take, CancellationToken ct = default);
}

/// <summary>Posición en una lista administrativa: fecha e id del último elemento entregado.</summary>
public sealed record AdminListPosition(DateTime At, Guid Id);
