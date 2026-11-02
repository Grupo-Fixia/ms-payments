using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Application.Services;

public interface IWalletService
{
    Task<WalletResponse> GetMyWalletAsync(Requester requester, CancellationToken ct = default);

    Task<WalletEntriesPage> GetMyEntriesAsync(Requester requester, int? limit, string? cursor,
        CancellationToken ct = default);
}

/// <summary>
/// Consulta de la billetera propia (GC-481). La billetera sale siempre del token (<c>sub</c>): no se recibe ningún id,
/// así que no hay forma de pedir la de otro usuario.
/// </summary>
public class WalletService : IWalletService
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    private readonly IWalletRepository _wallets;
    private readonly TimeProvider _clock;

    public WalletService(IWalletRepository wallets, TimeProvider clock)
    {
        _wallets = wallets;
        _clock = clock;
    }

    public async Task<WalletResponse> GetMyWalletAsync(Requester requester, CancellationToken ct = default)
    {
        var wallet = await OwnWalletAsync(requester, ct);
        return wallet.ToResponse();
    }

    public async Task<WalletEntriesPage> GetMyEntriesAsync(Requester requester, int? limit, string? cursor,
        CancellationToken ct = default)
    {
        var size = limit ?? DefaultLimit;
        if (size < 1 || size > MaxLimit)
            throw new InvalidQueryException($"El límite debe ser un número entre 1 y {MaxLimit}.");
        var after = string.IsNullOrWhiteSpace(cursor) ? null : WalletEntryCursor.Decode(cursor.Trim());

        var wallet = await OwnWalletAsync(requester, ct);

        // Se pide una fila de más para saber si hay otra página sin contar el total.
        var rows = await _wallets.ListEntriesAsync(wallet.Id, after, size + 1, ct);
        var page = rows.Take(size).ToList();
        var next = rows.Count > size
            ? WalletEntryCursor.Encode(new WalletEntryPosition(page[^1].CreatedAt, page[^1].Id))
            : null;

        return new WalletEntriesPage(page.Select(e => e.ToResponse()).ToList(), next);
    }

    private Task<Wallet> OwnWalletAsync(Requester requester, CancellationToken ct)
    {
        if (!requester.HasWallet)
            throw new PaymentAccessDeniedException("Solo clientes, técnicos y empresas tienen billetera.");

        return _wallets.GetOrCreateForUserAsync(requester.UserId, Wallet.DefaultCurrency,
            _clock.GetUtcNow().UtcDateTime, ct);
    }
}
