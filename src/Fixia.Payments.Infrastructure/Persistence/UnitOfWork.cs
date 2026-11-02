using Fixia.Payments.Application.Common.Interfaces;

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
}

