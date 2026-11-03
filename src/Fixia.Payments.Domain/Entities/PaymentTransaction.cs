using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Domain.Entities;

public class PaymentTransaction
{
    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public string TransactionType { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? GatewayTransactionId { get; private set; }
    public string? GatewayResponse { get; private set; }
    public DateTime CreatedAt { get; private set; }

    // Navigation property for EF Core
    public Payment? Payment { get; private set; }

    // Private constructor for EF Core
    private PaymentTransaction() { }

    public PaymentTransaction(
        Guid paymentId,
        string transactionType,
        decimal amount,
        PaymentStatus status,
        string? gatewayTransactionId = null,
        string? gatewayResponse = null)
    {
        Id = Guid.NewGuid();
        PaymentId = paymentId;
        TransactionType = transactionType;
        Amount = amount;
        Status = status;
        GatewayTransactionId = gatewayTransactionId;
        GatewayResponse = gatewayResponse;
        CreatedAt = DateTime.UtcNow;
    }
}

