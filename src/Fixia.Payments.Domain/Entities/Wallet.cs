using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Domain.Entities;

/// <summary>
/// Billetera simulada de un usuario o de Fixia (GC-480, modelo aprobado en GC-476). No representa dinero real.
/// <para>
/// Tiene dos saldos: <b>disponible</b> (lo que se puede usar o retirar) y <b>retenido</b> (lo reservado para un
/// servicio contratado hasta su cierre). Ninguno puede quedar negativo. Cada cambio de saldo deja un movimiento en el
/// libro (<see cref="WalletEntry"/>) con los saldos resultantes, así el saldo siempre se puede reconstruir.
/// </para>
/// </summary>
public class Wallet
{
    /// <summary>Dueño fijo de la cuenta de Fixia, la que recibe las comisiones (sembrada por la migración).</summary>
    public static readonly Guid PlatformOwnerId = Guid.Parse("00000000-0000-0000-0000-00000000f1a1");

    public const string DefaultCurrency = "COP";

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public WalletOwnerType OwnerType { get; private set; }
    public string Currency { get; private set; } = DefaultCurrency;
    public decimal AvailableBalance { get; private set; }
    public decimal HeldBalance { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    // Constructor para EF Core
    private Wallet() { }

    private Wallet(Guid id, Guid ownerId, WalletOwnerType ownerType, string currency, DateTime createdAt)
    {
        Id = id;
        OwnerId = ownerId;
        OwnerType = ownerType;
        Currency = currency;
        AvailableBalance = 0m;
        HeldBalance = 0m;
        CreatedAt = createdAt;
    }

    /// <summary>Billetera nueva de un usuario, en cero.</summary>
    public static Wallet OpenForUser(Guid userId, DateTime now, string currency = DefaultCurrency)
    {
        if (userId == Guid.Empty)
            throw new PaymentDomainException("El dueño de la billetera es obligatorio.");
        if (userId == PlatformOwnerId)
            throw new PaymentDomainException("Ese identificador está reservado para la cuenta de Fixia.");

        return new Wallet(Guid.NewGuid(), userId, WalletOwnerType.User, NormalizeCurrency(currency), now);
    }

    /// <summary>La cuenta de Fixia con un id conocido; solo la usa la migración que la siembra.</summary>
    public static Wallet Platform(Guid id, DateTime createdAt) =>
        new(id, PlatformOwnerId, WalletOwnerType.Platform, DefaultCurrency, createdAt);

    /// <summary>
    /// Aplica un movimiento y devuelve la línea del libro con los saldos resultantes. Si el resultado dejaría un saldo
    /// negativo no cambia nada y lanza <see cref="InsufficientBalanceException"/>.
    /// </summary>
    public WalletEntry Apply(WalletEntryType type, decimal amount, DateTime now, Guid? paymentId = null,
        string? reference = null)
    {
        Money.RequirePositive(amount);
        var (availableDelta, heldDelta) = Effect(type, amount);

        var available = AvailableBalance + availableDelta;
        var held = HeldBalance + heldDelta;
        if (available < 0)
            throw new InsufficientBalanceException(
                $"Saldo disponible insuficiente: hay {AvailableBalance:0.00} {Currency} y se necesitan {amount:0.00}.");
        if (held < 0)
            throw new InsufficientBalanceException(
                $"Saldo retenido insuficiente: hay {HeldBalance:0.00} {Currency} y se necesitan {amount:0.00}.");

        AvailableBalance = available;
        HeldBalance = held;
        UpdatedAt = now;

        return new WalletEntry(Id, type, amount, available, held, paymentId, reference, now);
    }

    /// <summary>Cómo mueve cada tipo de movimiento el saldo disponible y el retenido.</summary>
    public static (decimal Available, decimal Held) Effect(WalletEntryType type, decimal amount) => type switch
    {
        WalletEntryType.TopUp => (amount, 0m),
        WalletEntryType.Withdrawal => (-amount, 0m),
        WalletEntryType.Hold => (-amount, amount),
        WalletEntryType.Release => (amount, -amount),
        WalletEntryType.Capture => (0m, -amount),
        WalletEntryType.Credit => (amount, 0m),
        WalletEntryType.Commission => (amount, 0m),
        WalletEntryType.Refund => (amount, 0m),
        WalletEntryType.Reversal => (-amount, 0m),
        _ => throw new PaymentDomainException($"Tipo de movimiento no soportado: {type}.")
    };

    private static string NormalizeCurrency(string currency) =>
        string.IsNullOrWhiteSpace(currency) ? DefaultCurrency : currency.Trim().ToUpperInvariant();
}
