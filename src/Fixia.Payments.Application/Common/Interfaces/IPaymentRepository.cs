using Fixia.Payments.Domain.Entities;

namespace Fixia.Payments.Application.Common.Interfaces;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Payment?> GetByServiceRequestIdAsync(Guid serviceRequestId, CancellationToken ct = default);
    Task<IEnumerable<Payment>> ListByClientIdAsync(Guid clientId, CancellationToken ct = default);
    Task AddAsync(Payment payment, CancellationToken ct = default);
    void Update(Payment payment);
}

