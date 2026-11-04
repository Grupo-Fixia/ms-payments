using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Application;

/// <summary>El DTO dice explícitamente si hay retención y si se puede reintentar (pedido de GC-491).</summary>
public class PaymentMappingsTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ProviderId = Guid.NewGuid();

    private static Payment WalletPayment()
    {
        var rule = new CommissionRule(Guid.NewGuid(), 0.15m, DateTime.UtcNow.AddDays(-1), null, "15 %", DateTime.UtcNow);
        return Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), ClientId, ProviderId, rule.Quote(105000m, "COP"));
    }

    private static Requester Client => new(ClientId, new[] { "ROLE_CLIENT" });
    private static Requester Provider => new(ProviderId, new[] { "ROLE_PROFESSIONAL" });

    [Fact]
    public void WalletPayment_ShouldExposeBreakdownAndHoldStatus()
    {
        var payment = WalletPayment();
        payment.MarkAsProcessing("HOLD-1");

        var response = payment.ToResponse(Client);

        response.IsWalletPayment.Should().BeTrue();
        response.ContractId.Should().Be(payment.ContractId);
        response.Breakdown.Should().Be(new PaymentBreakdownResponse(105000m, 15750m, 0.15m, 120750m));
        response.HoldStatus.Should().Be(WalletHoldStatus.Held);
        response.CanRetryHold.Should().BeFalse();
    }

    [Fact]
    public void FailedHold_ShouldBeRetryableOnlyByThePayer()
    {
        var payment = WalletPayment();
        payment.MarkAsFailed("Saldo disponible insuficiente");

        payment.ToResponse(Client).HoldStatus.Should().Be(WalletHoldStatus.Failed);
        payment.ToResponse(Client).CanRetryHold.Should().BeTrue();
        payment.ToResponse(Provider).CanRetryHold.Should().BeFalse();
        payment.ToResponse(new Requester(Guid.NewGuid(), new[] { "ROLE_ADMIN" })).CanRetryHold.Should().BeFalse();
        payment.ToResponse().CanRetryHold.Should().BeFalse();
    }

    [Fact]
    public void HoldStatus_ShouldFollowTheWalletLifecycle()
    {
        var pending = WalletPayment();
        PaymentMappings.HoldStatusOf(pending).Should().Be(WalletHoldStatus.Pending);

        var captured = WalletPayment();
        captured.MarkAsProcessing("HOLD-1");
        captured.MarkAsCompleted("CAPTURE-1");
        PaymentMappings.HoldStatusOf(captured).Should().Be(WalletHoldStatus.Captured);

        var released = WalletPayment();
        released.MarkAsProcessing("HOLD-1");
        released.ReleaseHold("REF-1");
        PaymentMappings.HoldStatusOf(released).Should().Be(WalletHoldStatus.Released);

        var reversed = WalletPayment();
        reversed.MarkAsProcessing("HOLD-1");
        reversed.MarkAsCompleted("CAPTURE-1");
        reversed.Refund("REF-2");
        PaymentMappings.HoldStatusOf(reversed).Should().Be(WalletHoldStatus.Reversed);
    }

    [Fact]
    public void PaymentWithoutWallet_ShouldNotReportAHold_EvenIfFailed()
    {
        // Method = Wallet sin desglose (registrado por la ruta interna) tampoco cuenta como retención.
        var legacyWallet = new Payment(Guid.NewGuid(), ClientId, ProviderId, 50000m, "COP", PaymentMethod.Wallet);
        legacyWallet.MarkAsFailed("x");
        var card = new Payment(Guid.NewGuid(), ClientId, ProviderId, 50000m, "COP", PaymentMethod.CreditCard);
        card.MarkAsFailed("x");

        foreach (var payment in new[] { legacyWallet, card })
        {
            var response = payment.ToResponse(Client);
            response.IsWalletPayment.Should().BeFalse();
            response.Breakdown.Should().BeNull();
            response.HoldStatus.Should().Be(WalletHoldStatus.NotApplicable);
            response.CanRetryHold.Should().BeFalse();
        }
    }
}
