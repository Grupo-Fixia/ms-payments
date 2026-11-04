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

    // Desglose del pago con billetera (GC-480). Null en los pagos registrados antes por la ruta interna.

    /// <summary>Contrato de ms-services que originó el pago.</summary>
    public Guid? ContractId { get; private set; }

    /// <summary>Precio del prestador: lo que recibe íntegro al cerrar.</summary>
    public decimal? BaseAmount { get; private set; }

    /// <summary>Comisión de Fixia, a cargo del cliente. <c>Amount = BaseAmount + CommissionAmount</c>.</summary>
    public decimal? CommissionAmount { get; private set; }

    /// <summary>Tarifa aplicada y regla de la que salió, para reconstruir el cálculo aunque la tarifa cambie después.</summary>
    public decimal? CommissionRate { get; private set; }

    public Guid? CommissionRuleId { get; private set; }

    public IReadOnlyCollection<PaymentTransaction> Transactions => _transactions.AsReadOnly();

    /// <summary>
    /// Pago de un servicio contratado con billetera (GC-480/GC-484): <c>Amount</c> es el total que paga el cliente
    /// (precio + comisión) y queda el desglose con la regla aplicada.
    /// </summary>
    public static Payment ForContract(Guid serviceRequestId, Guid contractId, Guid clientId, Guid providerId,
        CommissionQuote quote)
    {
        if (contractId == Guid.Empty)
            throw new PaymentDomainException("El ContractId no puede estar vacío.");

        var payment = new Payment(serviceRequestId, clientId, providerId, quote.Total, quote.Currency,
            PaymentMethod.Wallet)
        {
            ContractId = contractId,
            BaseAmount = quote.Price,
            CommissionAmount = quote.Commission,
            CommissionRate = quote.Rate,
            CommissionRuleId = quote.RuleId
        };
        return payment;
    }

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

    /// <summary>Pago con billetera simulada (GC-480): se mueve por el libro de las billeteras, no por la pasarela.</summary>
    public bool IsWalletPayment => Method == PaymentMethod.Wallet && BaseAmount.HasValue;

    /// <summary>
    /// Libera al cliente lo retenido de un pago con billetera que todavía no se cobró (GC-487). En el modelo de billetera
    /// el estado <c>Processing</c> significa "retenido". Queda <c>Refunded</c>.
    /// </summary>
    public void ReleaseHold(string reference)
    {
        if (!IsWalletPayment)
            throw new PaymentDomainException("Solo un pago con billetera tiene saldo retenido que liberar.");
        if (Status != PaymentStatus.Processing)
            throw new InvalidPaymentStatusException(Status, "Liberar saldo retenido");

        Status = PaymentStatus.Refunded;
        UpdatedAt = DateTime.UtcNow;

        _transactions.Add(new PaymentTransaction(
            Id,
            "RELEASE",
            Amount,
            PaymentStatus.Refunded,
            reference
        ));
    }
}

