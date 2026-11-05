using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fixia.Payments.Infrastructure.Persistence.Repositories;

public class WalletRepository : IWalletRepository
{
    private readonly PaymentsDbContext _context;

    public WalletRepository(PaymentsDbContext context)
    {
        _context = context;
    }

    public Task<Wallet?> GetByOwnerAsync(Guid ownerId, string currency, CancellationToken ct = default) =>
        _context.Wallets.FirstOrDefaultAsync(w => w.OwnerId == ownerId && w.Currency == currency, ct);

    public async Task<Wallet> GetOrCreateForUserAsync(Guid userId, string currency, DateTime now,
        CancellationToken ct = default)
    {
        var existing = await GetByOwnerAsync(userId, currency, ct);
        if (existing != null)
            return existing;

        var wallet = Wallet.OpenForUser(userId, now, currency);
        _context.Wallets.Add(wallet);
        try
        {
            await _context.SaveChangesAsync(ct);
            return wallet;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation
                                           })
        {
            // Otra petición del mismo usuario la creó al mismo tiempo: se usa esa.
            _context.Entry(wallet).State = EntityState.Detached;
            return await _context.Wallets.SingleAsync(w => w.OwnerId == userId && w.Currency == currency, ct);
        }
    }

    public async Task<IReadOnlyList<WalletEntry>> ListEntriesAsync(Guid walletId, WalletEntryPosition? after,
        int take, CancellationToken ct = default)
    {
        var query = _context.WalletEntries.AsNoTracking().Where(e => e.WalletId == walletId);
        if (after != null)
        {
            query = query.Where(e => e.CreatedAt < after.CreatedAt
                                     || (e.CreatedAt == after.CreatedAt && e.Id.CompareTo(after.Id) < 0));
        }

        return await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task AddEntryAsync(WalletEntry entry, CancellationToken ct = default) =>
        await _context.WalletEntries.AddAsync(entry, ct);

    public Task<int> CountEntriesAsync(Guid walletId, WalletEntryType type, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default) =>
        _context.WalletEntries.AsNoTracking()
            .CountAsync(e => e.WalletId == walletId && e.Type == type && e.CreatedAt >= fromUtc && e.CreatedAt < toUtc,
                ct);
}
