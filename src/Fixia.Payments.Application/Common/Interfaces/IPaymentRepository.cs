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

    /// <summary>
    /// De <paramref name="serviceRequestIds"/>, las que tienen un pago del prestador con la retención pendiente o hecha
    /// (GC-524, contrato de empresas §6.2).
    /// </summary>
    /// <summary>
    /// Por cada solicitud pagada con billetera: su prestador y si el dinero sigue retenido (pendiente o retenido).
    /// Lo publica <c>pago.retencion</c> (GC-547) para que ms-users decida la desvinculación sin llamar a este servicio.
    /// </summary>
    Task<IReadOnlyList<HoldState>> ListHoldStatesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> ListHeldServiceRequestIdsAsync(Guid providerId, IReadOnlyCollection<Guid> serviceRequestIds,
        CancellationToken ct = default);
}

/// <summary>Filtros opcionales de la lista de pagos del ADMIN (contrato admin §4.1).</summary>
public sealed record AdminPaymentFilter(
    PaymentStatus? Status,
    DTOs.WalletHoldStatus? HoldStatus,
    Guid? ServiceRequestId,
    Guid? UserId);

/// <summary>Estado de retención de una solicitud (GC-547).</summary>
public sealed record HoldState(Guid ServiceRequestId, Guid ProviderId, bool Held);
