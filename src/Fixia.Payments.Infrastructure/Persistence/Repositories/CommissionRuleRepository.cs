using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.Infrastructure.Persistence.Repositories;

public class CommissionRuleRepository : ICommissionRuleRepository
{
    private readonly PaymentsDbContext _context;

    public CommissionRuleRepository(PaymentsDbContext context)
    {
        _context = context;
    }

    public Task<CommissionRule?> GetValidAtAsync(DateTime instant, CancellationToken ct = default) =>
        _context.CommissionRules
            .AsNoTracking()
            .Where(r => r.ValidFrom <= instant && (r.ValidTo == null || r.ValidTo > instant))
            .OrderByDescending(r => r.ValidFrom)
            .FirstOrDefaultAsync(ct);
}
