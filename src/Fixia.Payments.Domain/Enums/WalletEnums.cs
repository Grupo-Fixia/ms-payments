namespace Fixia.Payments.Domain.Enums;

/// <summary>Dueño de una billetera simulada (GC-480).</summary>
public enum WalletOwnerType
{
    /// <summary>Cliente, técnico o empresa: el <c>users.id</c> de ms-users.</summary>
    User = 1,

    /// <summary>La cuenta de Fixia, que recibe las comisiones.</summary>
    Platform = 2
}

/// <summary>
/// Tipos de movimiento del libro de la billetera (GC-480). Todo es dinero virtual: no hay transferencias reales.
/// Cada tipo mueve el saldo disponible y el retenido de una forma fija (ver <c>Wallet.Apply</c>).
/// </summary>
public enum WalletEntryType
{
    /// <summary>Recarga simulada del cliente: disponible ↑ (GC-482).</summary>
    TopUp = 1,

    /// <summary>Retiro simulado del técnico o empresa: disponible ↓ (GC-483).</summary>
    Withdrawal = 2,

    /// <summary>Retención al contratar: disponible ↓, retenido ↑ (GC-484).</summary>
    Hold = 3,

    /// <summary>Liberación de lo retenido: retenido ↓, disponible ↑ (GC-487).</summary>
    Release = 4,

    /// <summary>Cobro de lo retenido al cerrar: retenido ↓ (GC-485).</summary>
    Capture = 5,

    /// <summary>Abono del precio al prestador: disponible ↑ (GC-485).</summary>
    Credit = 6,

    /// <summary>Abono de la comisión a la cuenta de Fixia: disponible ↑ (GC-485).</summary>
    Commission = 7,

    /// <summary>Devolución al cliente de un pago ya cobrado: disponible ↑ (GC-487).</summary>
    Refund = 8,

    /// <summary>Reverso de un abono (prestador o Fixia) por reembolso: disponible ↓ (GC-487).</summary>
    Reversal = 9
}
