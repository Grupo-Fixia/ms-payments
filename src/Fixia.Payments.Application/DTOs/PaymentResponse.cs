using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Application.DTOs;

public record PaymentTransactionResponse(
    Guid Id,
    string TransactionType,
    decimal Amount,
    PaymentStatus Status,
    string? GatewayTransactionId,
    DateTime CreatedAt
);

/// <summary>Desglose del pago con billetera (GC-480): el prestador recibe el precio y el cliente paga el total.</summary>
public record PaymentBreakdownResponse(
    decimal Price,
    decimal Commission,
    decimal CommissionRate,
    decimal Total
);

/// <summary>
/// Estado de la retención en la billetera, para que el cliente (Flutter, GC-491) no tenga que deducirlo del método y
/// del estado del pago.
/// </summary>
public enum WalletHoldStatus
{
    /// <summary>No es un pago con billetera: no hay retención.</summary>
    NotApplicable,
    /// <summary>Registrado y todavía sin intentar la retención.</summary>
    Pending,
    /// <summary>El total está retenido en la billetera del cliente.</summary>
    Held,
    /// <summary>No se pudo retener (saldo insuficiente o sin billetera): el cliente recarga y reintenta.</summary>
    Failed,
    /// <summary>Cobrado al cerrar el servicio: el prestador y Fixia ya recibieron su parte.</summary>
    Captured,
    /// <summary>Un administrador liberó lo retenido antes del cobro.</summary>
    Released,
    /// <summary>Un administrador revirtió un pago ya cobrado.</summary>
    Reversed
}

public record PaymentResponse(
    Guid Id,
    Guid ServiceRequestId,
    Guid ClientId,
    Guid ProviderId,
    decimal Amount,
    string Currency,
    PaymentStatus Status,
    PaymentMethod Method,
    string? TransactionReference,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    IEnumerable<PaymentTransactionResponse> Transactions,
    Guid? ContractId = null,
    bool IsWalletPayment = false,
    PaymentBreakdownResponse? Breakdown = null,
    WalletHoldStatus HoldStatus = WalletHoldStatus.NotApplicable,
    bool CanRetryHold = false
);

public static class PaymentMappings
{
    /// <summary>
    /// <paramref name="viewer"/> es quien consulta: <c>CanRetryHold</c> solo es verdadero para el cliente que paga,
    /// porque es el único que puede reintentar la retención (<c>POST /api/payments/{id}/process</c>).
    /// </summary>
    public static PaymentResponse ToResponse(this Payment payment, Requester? viewer = null)
    {
        var isWallet = payment.IsWalletPayment;
        var holdStatus = HoldStatusOf(payment);

        return new PaymentResponse(
            payment.Id,
            payment.ServiceRequestId,
            payment.ClientId,
            payment.ProviderId,
            payment.Amount,
            payment.Currency,
            payment.Status,
            payment.Method,
            payment.TransactionReference,
            payment.FailureReason,
            payment.CreatedAt,
            payment.UpdatedAt,
            payment.Transactions.Select(t => new PaymentTransactionResponse(
                t.Id,
                t.TransactionType,
                t.Amount,
                t.Status,
                t.GatewayTransactionId,
                t.CreatedAt
            )).ToList(),
            payment.ContractId,
            isWallet,
            isWallet
                ? new PaymentBreakdownResponse(payment.BaseAmount!.Value, payment.CommissionAmount ?? 0m,
                    payment.CommissionRate ?? 0m, payment.Amount)
                : null,
            holdStatus,
            holdStatus == WalletHoldStatus.Failed && viewer is not null && viewer.IsPayerOf(payment)
        );
    }

    /// <summary>
    /// Con billetera, el estado del pago significa: <c>Processing</c> = retenido, <c>Completed</c> = cobrado,
    /// <c>Failed</c> = no se pudo retener y <c>Refunded</c> = liberado (antes del cobro) o revertido (después).
    /// </summary>
    public static WalletHoldStatus HoldStatusOf(Payment payment)
    {
        if (!payment.IsWalletPayment)
            return WalletHoldStatus.NotApplicable;

        return payment.Status switch
        {
            PaymentStatus.Pending => WalletHoldStatus.Pending,
            PaymentStatus.Processing => WalletHoldStatus.Held,
            PaymentStatus.Failed => WalletHoldStatus.Failed,
            PaymentStatus.Completed => WalletHoldStatus.Captured,
            PaymentStatus.Refunded => payment.Transactions.Any(t => t.TransactionType == "RELEASE")
                ? WalletHoldStatus.Released
                : WalletHoldStatus.Reversed,
            _ => WalletHoldStatus.NotApplicable
        };
    }
}
