using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Application.DTOs;
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

    public async Task<IReadOnlyList<Payment>> ListForAdminAsync(AdminPaymentFilter filter, AdminListPosition? after,
        int take, CancellationToken ct = default)
    {
        IQueryable<Payment> query = _context.Payments.AsNoTracking().Include(p => p.Transactions);

        if (filter.Status is { } status)
            query = query.Where(p => p.Status == status);
        if (filter.ServiceRequestId is { } serviceRequestId)
            query = query.Where(p => p.ServiceRequestId == serviceRequestId);
        if (filter.UserId is { } userId)
            query = query.Where(p => p.ClientId == userId || p.ProviderId == userId);
        if (filter.HoldStatus is { } hold)
            query = WhereHoldStatus(query, hold);

        if (after != null)
        {
            query = query.Where(p => p.CreatedAt < after.At
                                     || (p.CreatedAt == after.At && p.Id.CompareTo(after.Id) < 0));
        }

        return await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ListHeldServiceRequestIdsAsync(Guid providerId,
        IReadOnlyCollection<Guid> serviceRequestIds, CancellationToken ct = default)
    {
        var query = _context.Payments.AsNoTracking()
            .Where(p => p.ProviderId == providerId && serviceRequestIds.Contains(p.ServiceRequestId));
        var pending = WhereHoldStatus(query, WalletHoldStatus.Pending).Select(p => p.ServiceRequestId);
        var held = WhereHoldStatus(query, WalletHoldStatus.Held).Select(p => p.ServiceRequestId);
        return await pending.Union(held).OrderBy(id => id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldState>> ListHoldStatesAsync(CancellationToken ct = default)
    {
        var all = _context.Payments.AsNoTracking();
        var held = await WhereHoldStatus(all, WalletHoldStatus.Pending).Select(p => p.ServiceRequestId)
            .Union(WhereHoldStatus(all, WalletHoldStatus.Held).Select(p => p.ServiceRequestId))
            .ToListAsync(ct);
        var heldSet = held.ToHashSet();
        var wallet = await all.Where(p => p.Method == PaymentMethod.Wallet && p.BaseAmount != null)
            .Select(p => new { p.ServiceRequestId, p.ProviderId, p.CreatedAt })
            .ToListAsync(ct);
        return wallet
            .GroupBy(p => p.ServiceRequestId)
            .Select(group => group.OrderByDescending(p => p.CreatedAt).First())
            .Select(p => new HoldState(p.ServiceRequestId, p.ProviderId, heldSet.Contains(p.ServiceRequestId)))
            .OrderBy(state => state.ServiceRequestId)
            .ToList();
    }

    /// <summary>El mismo criterio que <c>PaymentMappings.HoldStatusOf</c>, traducido a SQL.</summary>
    private static IQueryable<Payment> WhereHoldStatus(IQueryable<Payment> query, WalletHoldStatus hold)
    {
        var wallet = query.Where(p => p.Method == PaymentMethod.Wallet && p.BaseAmount != null);
        return hold switch
        {
            WalletHoldStatus.NotApplicable => query.Where(p => p.Method != PaymentMethod.Wallet || p.BaseAmount == null),
            WalletHoldStatus.Pending => wallet.Where(p => p.Status == PaymentStatus.Pending),
            WalletHoldStatus.Held => wallet.Where(p => p.Status == PaymentStatus.Processing),
            WalletHoldStatus.Failed => wallet.Where(p => p.Status == PaymentStatus.Failed),
            WalletHoldStatus.Captured => wallet.Where(p => p.Status == PaymentStatus.Completed),
            WalletHoldStatus.Released => wallet.Where(p => p.Status == PaymentStatus.Refunded
                && p.Transactions.Any(t => t.TransactionType == "RELEASE")),
            WalletHoldStatus.Reversed => wallet.Where(p => p.Status == PaymentStatus.Refunded
                && !p.Transactions.Any(t => t.TransactionType == "RELEASE")),
            _ => query
        };
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
