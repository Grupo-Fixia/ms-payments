using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Domain;

public class WalletTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);

    private static Wallet WalletWith(decimal available)
    {
        var wallet = Wallet.OpenForUser(Guid.NewGuid(), Now);
        if (available > 0)
            wallet.Apply(WalletEntryType.TopUp, available, Now);
        return wallet;
    }

    [Fact]
    public void OpenForUser_ShouldStartAtZeroInCop()
    {
        var owner = Guid.NewGuid();

        var wallet = Wallet.OpenForUser(owner, Now);

        wallet.OwnerId.Should().Be(owner);
        wallet.OwnerType.Should().Be(WalletOwnerType.User);
        wallet.Currency.Should().Be("COP");
        wallet.AvailableBalance.Should().Be(0m);
        wallet.HeldBalance.Should().Be(0m);
    }

    [Fact]
    public void OpenForUser_WithPlatformOrEmptyOwner_ShouldBeRejected()
    {
        FluentActions.Invoking(() => Wallet.OpenForUser(Wallet.PlatformOwnerId, Now)).Should().Throw<PaymentDomainException>();
        FluentActions.Invoking(() => Wallet.OpenForUser(Guid.Empty, Now)).Should().Throw<PaymentDomainException>();
    }

    [Theory]
    [InlineData(WalletEntryType.TopUp, 100, 150, 0)]
    [InlineData(WalletEntryType.Withdrawal, 30, 20, 0)]
    [InlineData(WalletEntryType.Hold, 40, 10, 40)]
    [InlineData(WalletEntryType.Credit, 5, 55, 0)]
    [InlineData(WalletEntryType.Commission, 5, 55, 0)]
    [InlineData(WalletEntryType.Refund, 5, 55, 0)]
    [InlineData(WalletEntryType.Reversal, 50, 0, 0)]
    public void Apply_ShouldMoveBalancesAccordingToTheType(WalletEntryType type, decimal amount,
        decimal expectedAvailable, decimal expectedHeld)
    {
        var wallet = WalletWith(50m);

        var entry = wallet.Apply(type, amount, Now.AddMinutes(1), Guid.NewGuid(), "ref");

        wallet.AvailableBalance.Should().Be(expectedAvailable);
        wallet.HeldBalance.Should().Be(expectedHeld);
        entry.Type.Should().Be(type);
        entry.Amount.Should().Be(amount);
        entry.AvailableAfter.Should().Be(expectedAvailable);
        entry.HeldAfter.Should().Be(expectedHeld);
        entry.WalletId.Should().Be(wallet.Id);
        entry.Reference.Should().Be("ref");
    }

    [Fact]
    public void HoldReleaseAndCapture_ShouldMoveMoneyBetweenAvailableAndHeld()
    {
        var wallet = WalletWith(120750m);

        wallet.Apply(WalletEntryType.Hold, 120750m, Now);
        wallet.AvailableBalance.Should().Be(0m);
        wallet.HeldBalance.Should().Be(120750m);

        wallet.Apply(WalletEntryType.Release, 750m, Now);
        wallet.AvailableBalance.Should().Be(750m);
        wallet.HeldBalance.Should().Be(120000m);

        wallet.Apply(WalletEntryType.Capture, 120000m, Now);
        wallet.AvailableBalance.Should().Be(750m);
        wallet.HeldBalance.Should().Be(0m);
    }

    [Theory]
    [InlineData(WalletEntryType.Withdrawal, 50.01)]
    [InlineData(WalletEntryType.Hold, 60)]
    [InlineData(WalletEntryType.Release, 1)]
    [InlineData(WalletEntryType.Capture, 1)]
    [InlineData(WalletEntryType.Reversal, 70)]
    public void Apply_ThatWouldLeaveANegativeBalance_ShouldChangeNothing(WalletEntryType type, decimal amount)
    {
        var wallet = WalletWith(50m);

        var action = () => wallet.Apply(type, amount, Now);

        action.Should().Throw<InsufficientBalanceException>();
        wallet.AvailableBalance.Should().Be(50m);
        wallet.HeldBalance.Should().Be(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.001)]
    public void Apply_WithInvalidAmount_ShouldBeRejected(decimal amount)
    {
        var wallet = WalletWith(50m);

        FluentActions.Invoking(() => wallet.Apply(WalletEntryType.TopUp, amount, Now))
            .Should().Throw<PaymentDomainException>();
        wallet.AvailableBalance.Should().Be(50m);
    }

    [Fact]
    public void Entries_ShouldReconcileWithTheFinalBalance()
    {
        var wallet = Wallet.OpenForUser(Guid.NewGuid(), Now);
        var entries = new[]
        {
            wallet.Apply(WalletEntryType.TopUp, 200000m, Now),
            wallet.Apply(WalletEntryType.Hold, 120750m, Now),
            wallet.Apply(WalletEntryType.Release, 120750m, Now),
            wallet.Apply(WalletEntryType.Withdrawal, 50000.50m, Now)
        };

        var available = entries.Sum(e => Wallet.Effect(e.Type, e.Amount).Available);
        var held = entries.Sum(e => Wallet.Effect(e.Type, e.Amount).Held);

        available.Should().Be(wallet.AvailableBalance).And.Be(149999.50m);
        held.Should().Be(wallet.HeldBalance).And.Be(0m);
        entries[^1].AvailableAfter.Should().Be(wallet.AvailableBalance);
    }

    [Fact]
    public void Entry_ShouldStoreMicrosecondPrecision()
    {
        var wallet = Wallet.OpenForUser(Guid.NewGuid(), Now);

        var entry = wallet.Apply(WalletEntryType.TopUp, 1m, Now.AddTicks(1234567));

        (entry.CreatedAt.Ticks % 10).Should().Be(0);
        entry.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }
}
