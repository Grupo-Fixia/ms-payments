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
}

