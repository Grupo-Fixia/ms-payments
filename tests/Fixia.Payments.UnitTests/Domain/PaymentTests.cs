using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace Fixia.Payments.UnitTests.Domain;

public class PaymentTests
{
    [Fact]
    public void ReleaseHold_OfAHeldWalletPayment_ShouldRefundIt_AndOnlyFromHeld()
    {
        // GC-487: con billetera, Processing significa retenido.
        var rule = new CommissionRule(Guid.NewGuid(), 0.15m, DateTime.UtcNow.AddDays(-1), null, "15 %", DateTime.UtcNow);
        var payment = Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            rule.Quote(105000m, "COP"));

        FluentActions.Invoking(() => payment.ReleaseHold("REF-0")).Should().Throw<InvalidPaymentStatusException>();

        payment.MarkAsProcessing("HOLD-1");
        payment.ReleaseHold("REF-1");

        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.Transactions.Last().TransactionType.Should().Be("RELEASE");
        FluentActions.Invoking(() => payment.ReleaseHold("REF-2")).Should().Throw<InvalidPaymentStatusException>();
    }

    [Fact]
    public void ReleaseHold_OfAPaymentWithoutWallet_ShouldBeRejected()
    {
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50000m, "COP", PaymentMethod.CreditCard);
        payment.MarkAsProcessing("TX-1");

        FluentActions.Invoking(() => payment.ReleaseHold("REF-1")).Should().Throw<PaymentDomainException>();
    }

    [Fact]
    public void CreatePayment_WithValidData_ShouldInitializeInPendingStatus()
    {
        // Arrange
        var serviceRequestId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var amount = 150000m;

        // Act
        var payment = new Payment(
            serviceRequestId,
            clientId,
            providerId,
            amount,
            "COP",
            PaymentMethod.CreditCard
        );

        // Assert
        payment.Id.Should().NotBeEmpty();
        payment.ServiceRequestId.Should().Be(serviceRequestId);
        payment.ClientId.Should().Be(clientId);
        payment.ProviderId.Should().Be(providerId);
        payment.Amount.Should().Be(amount);
        payment.Currency.Should().Be("COP");
        payment.Method.Should().Be(PaymentMethod.CreditCard);
        payment.Status.Should().Be(PaymentStatus.Pending);
        payment.Transactions.Should().BeEmpty();
    }

    [Fact]
    public void CreatePayment_WithNegativeOrZeroAmount_ShouldThrowPaymentDomainException()
    {
        // Arrange & Act
        var action = () => new Payment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0m,
            "COP",
            PaymentMethod.DebitCard
        );

        // Assert
        action.Should().Throw<PaymentDomainException>()
            .WithMessage("*monto*");
    }

    [Fact]
    public void MarkAsProcessing_WhenPending_ShouldUpdateStatusAndReference()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50000m, "COP", PaymentMethod.CreditCard);

        // Act
        payment.MarkAsProcessing("TX-12345");

        // Assert
        payment.Status.Should().Be(PaymentStatus.Processing);
        payment.TransactionReference.Should().Be("TX-12345");
        payment.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsCompleted_WhenProcessing_ShouldUpdateStatusAndRecordTransaction()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50000m, "COP", PaymentMethod.CreditCard);
        payment.MarkAsProcessing("TX-12345");

        // Act
        payment.MarkAsCompleted("GW-FINAL-REF", "{\"approved\":true}");

        // Assert
        payment.Status.Should().Be(PaymentStatus.Completed);
        payment.TransactionReference.Should().Be("GW-FINAL-REF");
        payment.Transactions.Should().HaveCount(1);
        var tx = payment.Transactions.First();
        tx.TransactionType.Should().Be("CHARGE");
        tx.Status.Should().Be(PaymentStatus.Completed);
        tx.GatewayTransactionId.Should().Be("GW-FINAL-REF");
    }

    [Fact]
    public void Refund_WhenNotCompleted_ShouldThrowInvalidPaymentStatusException()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50000m, "COP", PaymentMethod.CreditCard);

        // Act
        var action = () => payment.Refund("REF-123");

        // Assert
        action.Should().Throw<InvalidPaymentStatusException>()
            .WithMessage("*Reembolsar pago*");
    }

    [Fact]
    public void Refund_WhenCompleted_ShouldUpdateStatusToRefundedAndRecordTransaction()
    {
        // Arrange
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50000m, "COP", PaymentMethod.CreditCard);
        payment.MarkAsCompleted("TX-OK");

        // Act
        payment.Refund("REF-999");

        // Assert
        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.Transactions.Should().HaveCount(2); // Charge + Refund
        var refundTx = payment.Transactions.Last();
        refundTx.TransactionType.Should().Be("REFUND");
        refundTx.Status.Should().Be(PaymentStatus.Refunded);
    }
}

