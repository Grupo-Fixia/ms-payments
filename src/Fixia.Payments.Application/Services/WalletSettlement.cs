using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Application.Services;

/// <summary>
/// Movimientos de dinero virtual de un pago con billetera (GC-484, GC-485). Solo cambia entidades en memoria: quien lo
/// usa agrega los movimientos y guarda todo en una sola transacción.
/// </summary>
public static class WalletSettlement
{
    /// <summary>
    /// Retiene el total (precio + comisión) del disponible del cliente. Si no alcanza (o el cliente aún no tiene
    /// billetera) no mueve nada y devuelve el motivo.
    /// </summary>
    public static (WalletEntry? Entry, string? FailureReason) TryHold(Payment payment, Wallet? clientWallet, DateTime now)
    {
        var available = clientWallet?.AvailableBalance ?? 0m;
        if (clientWallet is null || available < payment.Amount)
            return (null, $"Saldo disponible insuficiente: hay {available:0.00} {payment.Currency} y se necesitan {payment.Amount:0.00}.");

        var reference = $"HOLD-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        var entry = clientWallet.Apply(WalletEntryType.Hold, payment.Amount, now, payment.Id, reference);
        payment.MarkAsProcessing(reference);
        return (entry, null);
    }

    /// <summary>
    /// Cobra lo retenido al cerrar: el cliente pierde el total retenido, el prestador recibe el precio íntegro y Fixia
    /// la comisión. Siempre <c>total = precio + comisión</c>.
    /// </summary>
    public static IReadOnlyList<WalletEntry> Capture(Payment payment, Wallet client, Wallet provider, Wallet platform,
        DateTime now)
    {
        var reference = $"CAPTURE-{Guid.NewGuid():N}"[..27].ToUpperInvariant();
        var entries = new List<WalletEntry>
        {
            client.Apply(WalletEntryType.Capture, payment.Amount, now, payment.Id, reference),
            provider.Apply(WalletEntryType.Credit, payment.BaseAmount!.Value, now, payment.Id, reference)
        };
        if (payment.CommissionAmount is > 0m)
            entries.Add(platform.Apply(WalletEntryType.Commission, payment.CommissionAmount.Value, now, payment.Id, reference));

        payment.MarkAsCompleted(reference);
        return entries;
    }
}
