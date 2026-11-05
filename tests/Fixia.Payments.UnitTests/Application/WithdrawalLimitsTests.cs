using Fixia.Payments.Application.Services;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Application;

/// <summary>El día del límite de retiros (GC-483) es el de Colombia, no el de UTC.</summary>
public class WithdrawalLimitsTests
{
    [Fact]
    public void DayOf_ShouldStartAndEndAtMidnightInBogota()
    {
        var limits = new WithdrawalLimits();

        // 4 de noviembre a las 23:30 en Bogotá = 5 de noviembre a las 04:30 UTC.
        var (from, to) = limits.DayOf(new DateTime(2026, 11, 5, 4, 30, 0, DateTimeKind.Utc));

        from.Should().Be(new DateTime(2026, 11, 4, 5, 0, 0, DateTimeKind.Utc));
        to.Should().Be(new DateTime(2026, 11, 5, 5, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void DayOf_JustAfterMidnightInBogota_ShouldBeTheNextDay()
    {
        var (from, _) = new WithdrawalLimits().DayOf(new DateTime(2026, 11, 5, 5, 0, 0, DateTimeKind.Utc));

        from.Should().Be(new DateTime(2026, 11, 5, 5, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxPerDay_BelowOne_ShouldBeRejected(int max)
    {
        var create = () => new WithdrawalLimits(max);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
