using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Entities;

namespace Fixia.Payments.Application.Common.Interfaces;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Payment?> GetByServiceRequestIdAsync(Guid serviceRequestId, CancellationToken ct = default);
    Task<IEnumerable<Payment>> ListByClientIdAsync(Guid clientId, CancellationToken ct = default);
    Task AddAsync(Payment payment, CancellationToken ct = default);
    void Update(Payment payment);

    /// <summary>
    /// Pagos para el ADMIN (GC-535), del más reciente al más antiguo (fecha e id descendentes), después de
    /// <paramref name="after"/>, con sus transacciones (las necesita el estado de la retención).
    /// </summary>
    Task<IReadOnlyList<Payment>> ListForAdminAsync(AdminPaymentFilter filter, AdminListPosition? after, int take,
        CancellationToken ct = default);
}

/// <summary>Filtros opcionales de la lista de pagos del ADMIN (contrato admin §4.1).</summary>
public sealed record AdminPaymentFilter(
    PaymentStatus? Status,
    DTOs.WalletHoldStatus? HoldStatus,
    Guid? ServiceRequestId,
    Guid? UserId);

