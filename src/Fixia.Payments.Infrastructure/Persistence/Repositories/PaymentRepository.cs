using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.Infrastructure.Persistence.Repositories;

public class PaymentRepository : IPaymentRepository
{
    private readonly PaymentsDbContext _context;

    public PaymentRepository(PaymentsDbContext context)
    {
        _context = context;
    }

    public async Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Payments
            .Include(p => p.Transactions)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Payment?> GetByServiceRequestIdAsync(Guid serviceRequestId, CancellationToken ct = default)
    {
        return await _context.Payments
            .Include(p => p.Transactions)
            .FirstOrDefaultAsync(p => p.ServiceRequestId == serviceRequestId, ct);
    }

    public async Task<IEnumerable<Payment>> ListByClientIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return await _context.Payments
            .Include(p => p.Transactions)
            .Where(p => p.ClientId == clientId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task AddAsync(Payment payment, CancellationToken ct = default)
    {
        await _context.Payments.AddAsync(payment, ct);
    }

    public void Update(Payment payment)
    {
        var entry = _context.Entry(payment);
        if (entry.State == EntityState.Detached)
        {
            _context.Payments.Attach(payment);
            entry.State = EntityState.Modified;
        }

        foreach (var tx in payment.Transactions)
        {
            var txEntry = _context.Entry(tx);
            if (txEntry.State == EntityState.Detached)
            {
                _context.PaymentTransactions.Add(tx);
            }
        }
    }
}
