using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Domain;

public class CommissionRuleTests
{
    private static readonly DateTime From = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private static CommissionRule Rule(decimal rate = 0.15m, DateTime? validTo = null) =>
        new(Guid.NewGuid(), rate, From, validTo, "prueba", From);

    [Theory]
    [InlineData(105000, 15750, 120750)]
    [InlineData(80000, 12000, 92000)]
    [InlineData(333.33, 50.00, 383.33)]   // 49,9995 se redondea a 50,00
    [InlineData(0.03, 0.00, 0.03)]        // 0,0045 se redondea a 0,00
    [InlineData(0.04, 0.01, 0.05)]        // 0,006 se redondea a 0,01
    public void Quote_ShouldAddTheCommissionOnTopOfThePriceWithExactDecimals(decimal price, decimal commission,
        decimal total)
    {
        var quote = Rule().Quote(price, "cop");

        quote.Price.Should().Be(price);
        quote.Commission.Should().Be(commission);
        quote.Total.Should().Be(total);
        (quote.Price + quote.Commission).Should().Be(quote.Total);
        quote.Currency.Should().Be("COP");
        quote.Rate.Should().Be(0.15m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Rule_WithRateOutOfRange_ShouldBeRejected(decimal rate)
    {
        FluentActions.Invoking(() => Rule(rate)).Should().Throw<PaymentDomainException>();
    }

    [Fact]
    public void Rule_ShouldBeValidOnlyInsideItsPeriod()
    {
        var rule = Rule(validTo: From.AddDays(30));

        rule.IsValidAt(From.AddSeconds(-1)).Should().BeFalse();
        rule.IsValidAt(From).Should().BeTrue();
        rule.IsValidAt(From.AddDays(29)).Should().BeTrue();
        rule.IsValidAt(From.AddDays(30)).Should().BeFalse();
    }

    [Fact]
    public void PaymentForContract_ShouldKeepTheBreakdownAndTheAppliedRule()
    {
        var quote = Rule().Quote(105000m, "COP");
        var contractId = Guid.NewGuid();

        var payment = Payment.ForContract(Guid.NewGuid(), contractId, Guid.NewGuid(), Guid.NewGuid(), quote);

        payment.Amount.Should().Be(120750m);
        payment.BaseAmount.Should().Be(105000m);
        payment.CommissionAmount.Should().Be(15750m);
        payment.CommissionRate.Should().Be(0.15m);
        payment.CommissionRuleId.Should().Be(quote.RuleId);
        payment.ContractId.Should().Be(contractId);
        payment.Method.Should().Be(PaymentMethod.Wallet);
        payment.Status.Should().Be(PaymentStatus.Pending);
    }
}
