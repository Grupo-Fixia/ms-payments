using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Application.DTOs;

/// <summary>Saldos de la billetera propia (GC-481). Dinero virtual.</summary>
public record WalletResponse(
    Guid WalletId,
    decimal AvailableBalance,
    decimal HeldBalance,
    string Currency,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>Una línea del libro: el importe es siempre positivo y la dirección la da el tipo.</summary>
public record WalletEntryResponse(
    Guid Id,
    WalletEntryType Type,
    decimal Amount,
    decimal AvailableAfter,
    decimal HeldAfter,
    Guid? PaymentId,
    string? Reference,
    DateTime CreatedAt
);

/// <summary>Página de movimientos, con la misma forma que el historial de ms-services: <c>nextCursor</c> es null al final.</summary>
public record WalletEntriesPage(IReadOnlyList<WalletEntryResponse> Items, string? NextCursor);

/// <summary>
/// Desglose de un pago antes de aceptar la cotización (GC-471, criterio 1): el cliente paga <c>Total</c> y el
/// prestador recibe <c>Price</c> íntegro.
/// </summary>
public record CommissionQuoteResponse(
    decimal Price,
    decimal Commission,
    decimal Total,
    string Currency,
    decimal Rate,
    Guid RuleId,
    DateTime RuleValidFrom
);

/// <summary>Recarga o retiro simulado (GC-482, GC-483): importe positivo con dos decimales como máximo, en COP.</summary>
public record WalletAmountRequest(decimal Amount, string? Currency = null);

/// <summary>Resultado de una recarga o retiro: el movimiento creado y los saldos que quedaron.</summary>
public record WalletOperationResponse(
    Guid EntryId,
    WalletEntryType Type,
    decimal Amount,
    string Currency,
    decimal AvailableBalance,
    decimal HeldBalance,
    string GatewayReference,
    DateTime CreatedAt
);

public static class WalletMappings
{
    public static WalletResponse ToResponse(this Wallet wallet) =>
        new(wallet.Id, wallet.AvailableBalance, wallet.HeldBalance, wallet.Currency, wallet.CreatedAt, wallet.UpdatedAt);

    public static WalletEntryResponse ToResponse(this WalletEntry entry) =>
        new(entry.Id, entry.Type, entry.Amount, entry.AvailableAfter, entry.HeldAfter, entry.PaymentId,
            entry.Reference, entry.CreatedAt);
}
