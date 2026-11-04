using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Exceptions;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Application;

public class WalletEntryCursorTests
{
    [Fact]
    public void EncodeAndDecode_ShouldRoundTrip()
    {
        var position = new WalletEntryPosition(new DateTime(2026, 11, 2, 20, 3, 4, 567, DateTimeKind.Utc).AddTicks(890),
            Guid.NewGuid());

        var cursor = WalletEntryCursor.Encode(position);

        cursor.Should().MatchRegex("^[A-Za-z0-9_-]+$", "debe viajar en la URL sin escaparse");
        WalletEntryCursor.Decode(cursor).Should().Be(position);
    }

    [Theory]
    [InlineData("no-es-un-cursor")]
    [InlineData("MTIzOmFiYw")]   // "123:abc"
    [InlineData("%%%")]
    public void Decode_WithForeignText_ShouldBeRejected(string cursor)
    {
        FluentActions.Invoking(() => WalletEntryCursor.Decode(cursor))
            .Should().Throw<InvalidQueryException>().WithMessage("*cursor*");
    }
}
