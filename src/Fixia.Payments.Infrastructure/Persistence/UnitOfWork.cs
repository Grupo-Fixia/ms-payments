using Fixia.Payments.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly PaymentsDbContext _context;

    public UnitOfWork(PaymentsDbContext context)
    {
        _context = context;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _context.SaveChangesAsync(ct);
    }

    public bool IsConcurrencyConflict(Exception exception) => exception is DbUpdateConcurrencyException;

    public void DiscardChanges() => _context.ChangeTracker.Clear();
}

