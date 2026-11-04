using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Domain.Entities;

/// <summary>
/// Línea del libro de una billetera (GC-480). Es inmutable: guarda el tipo, el importe (siempre positivo; la dirección
/// la da el tipo) y los dos saldos que quedaron después, para poder auditar y reconstruir cualquier saldo.
/// </summary>
public class WalletEntry
{
    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public WalletEntryType Type { get; private set; }
    public decimal Amount { get; private set; }
    public decimal AvailableAfter { get; private set; }
    public decimal HeldAfter { get; private set; }

    /// <summary>Pago que originó el movimiento, si lo hay (retener, cobrar, liberar, reembolsar).</summary>
    public Guid? PaymentId { get; private set; }

    /// <summary>Referencia externa del mock de pasarela o del proceso que lo originó.</summary>
    public string? Reference { get; private set; }

    public DateTime CreatedAt { get; private set; }

    // Constructor para EF Core
    private WalletEntry() { }

    internal WalletEntry(Guid walletId, WalletEntryType type, decimal amount, decimal availableAfter,
        decimal heldAfter, Guid? paymentId, string? reference, DateTime createdAt)
    {
        Id = Guid.NewGuid();
        WalletId = walletId;
        Type = type;
        Amount = amount;
        AvailableAfter = availableAfter;
        HeldAfter = heldAfter;
        PaymentId = paymentId;
        Reference = reference;
        // PostgreSQL guarda microsegundos: así el valor en memoria y el guardado son idénticos y el cursor de la
        // lista de movimientos no se corre ni repite filas.
        CreatedAt = new DateTime(createdAt.Ticks - createdAt.Ticks % 10, DateTimeKind.Utc);
    }
}
