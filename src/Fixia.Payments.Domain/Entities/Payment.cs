using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Domain.Entities;

public class Payment
{
    private readonly List<PaymentTransaction> _transactions = new();

    public Guid Id { get; private set; }
    public Guid ServiceRequestId { get; private set; }
    public Guid ClientId { get; private set; }
    public Guid ProviderId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "COP";
    public PaymentStatus Status { get; private set; }
    public PaymentMethod Method { get; private set; }
    public string? TransactionReference { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public IReadOnlyCollection<PaymentTransaction> Transactions => _transactions.AsReadOnly();

    // Private constructor for EF Core
    private Payment() { }

    public Payment(
        Guid serviceRequestId,
        Guid clientId,
        Guid providerId,
        decimal amount,
        string currency,
        PaymentMethod method)
    {
        if (serviceRequestId == Guid.Empty)
            throw new PaymentDomainException("El ServiceRequestId no puede estar vacío.");

        if (clientId == Guid.Empty)
            throw new PaymentDomainException("El ClientId no puede estar vacío.");

        if (providerId == Guid.Empty)
            throw new PaymentDomainException("El ProviderId no puede estar vacío.");

        if (amount <= 0)
            throw new PaymentDomainException("El monto a pagar debe ser mayor a cero.");

        Id = Guid.NewGuid();
        ServiceRequestId = serviceRequestId;
        ClientId = clientId;
        ProviderId = providerId;
        Amount = amount;
        Currency = string.IsNullOrWhiteSpace(currency) ? "COP" : currency.ToUpperInvariant();
        Method = method;
        Status = PaymentStatus.Pending;
        CreatedAt = DateTime.UtcNow;
    }

    public void MarkAsProcessing(string transactionReference)
    {
        if (Status != PaymentStatus.Pending && Status != PaymentStatus.Failed)
            throw new InvalidPaymentStatusException(Status, "Procesar pago");

        Status = PaymentStatus.Processing;
        TransactionReference = transactionReference;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsCompleted(string transactionReference, string? gatewayResponse = null)
    {
        if (Status != PaymentStatus.Processing && Status != PaymentStatus.Pending)
            throw new InvalidPaymentStatusException(Status, "Completar pago");

        Status = PaymentStatus.Completed;
        TransactionReference = transactionReference;
        FailureReason = null;
        UpdatedAt = DateTime.UtcNow;

        _transactions.Add(new PaymentTransaction(
            Id,
            "CHARGE",
            Amount,
            PaymentStatus.Completed,
            transactionReference,
            gatewayResponse
        ));
    }

    public void MarkAsFailed(string reason, string? gatewayResponse = null)
    {
        if (Status == PaymentStatus.Completed || Status == PaymentStatus.Refunded)
            throw new InvalidPaymentStatusException(Status, "Marcar pago como fallido");

        Status = PaymentStatus.Failed;
        FailureReason = reason;
        UpdatedAt = DateTime.UtcNow;

        _transactions.Add(new PaymentTransaction(
            Id,
            "CHARGE",
            Amount,
            PaymentStatus.Failed,
            TransactionReference,
            gatewayResponse ?? reason
        ));
    }

    public void Refund(string refundReference, string? gatewayResponse = null)
    {
        if (Status != PaymentStatus.Completed)
            throw new InvalidPaymentStatusException(Status, "Reembolsar pago");

        Status = PaymentStatus.Refunded;
        UpdatedAt = DateTime.UtcNow;

        _transactions.Add(new PaymentTransaction(
            Id,
            "REFUND",
            Amount,
            PaymentStatus.Refunded,
            refundReference,
            gatewayResponse
        ));
    }
}

