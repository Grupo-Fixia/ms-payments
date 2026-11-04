using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Application.Common.Interfaces;

public record PaymentGatewayResult(
    bool IsSuccess,
    string TransactionReference,
    string? ErrorMessage = null,
    string? RawResponse = null
);

public interface IPaymentGateway
{
    Task<PaymentGatewayResult> ProcessChargeAsync(
        Guid paymentId,
        decimal amount,
        string currency,
        PaymentMethod method,
        CancellationToken ct = default);

    Task<PaymentGatewayResult> ProcessRefundAsync(
        Guid paymentId,
        string transactionReference,
        decimal amount,
        CancellationToken ct = default);

    /// <summary>Recarga simulada de una billetera (GC-482): la pasarela "cobra" al cliente y aprueba o rechaza.</summary>
    Task<PaymentGatewayResult> ProcessTopUpAsync(
        Guid walletId,
        decimal amount,
        string currency,
        CancellationToken ct = default);

    /// <summary>Retiro simulado de una billetera (GC-483): la pasarela "paga" al técnico o empresa.</summary>
    Task<PaymentGatewayResult> ProcessPayoutAsync(
        Guid walletId,
        decimal amount,
        string currency,
        CancellationToken ct = default);
}

