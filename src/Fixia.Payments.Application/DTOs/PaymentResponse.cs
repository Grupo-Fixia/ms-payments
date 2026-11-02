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
    IEnumerable<PaymentTransactionResponse> Transactions
);

public static class PaymentMappings
{
    public static PaymentResponse ToResponse(this Payment payment)
    {
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
            )).ToList()
        );
    }
}

