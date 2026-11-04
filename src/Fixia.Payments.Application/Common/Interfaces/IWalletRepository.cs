using Fixia.Payments.Domain.Entities;

namespace Fixia.Payments.Application.Common.Interfaces;

/// <summary>Billeteras y su libro de movimientos (GC-480/GC-481).</summary>
public interface IWalletRepository
{
    Task<Wallet?> GetByOwnerAsync(Guid ownerId, string currency, CancellationToken ct = default);

    /// <summary>
    /// La billetera del usuario; si no existe, la crea en cero y la guarda. Si otra petición la creó al mismo tiempo,
    /// devuelve esa (restricción única por dueño y moneda).
    /// </summary>
    Task<Wallet> GetOrCreateForUserAsync(Guid userId, string currency, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Movimientos del más reciente al más antiguo, a partir de la posición <paramref name="after"/> (excluida).
    /// Devuelve hasta <paramref name="take"/> filas.
    /// </summary>
    Task<IReadOnlyList<WalletEntry>> ListEntriesAsync(Guid walletId, WalletEntryPosition? after, int take,
        CancellationToken ct = default);

    Task AddEntryAsync(WalletEntry entry, CancellationToken ct = default);
}

/// <summary>Posición en el libro: fecha y id del último movimiento de la página anterior.</summary>
public sealed record WalletEntryPosition(DateTime CreatedAt, Guid Id);
