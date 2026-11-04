using Fixia.Payments.Infrastructure.Persistence.Outbox;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Infrastructure;

public class OutboxEventTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);

    private static OutboxEvent NewEvent(long sequence = 1) => OutboxEvent.Pending(
        Guid.NewGuid(), "pago.creado", 1, OutboxEvent.AggregateTypePayment, Guid.NewGuid(), sequence,
        "fixia.payments.events", Guid.NewGuid().ToString(), "{\"eventType\":\"pago.creado\"}", Now);

    [Fact]
    public void Pending_ShouldStartPendingAndDueImmediately()
    {
        var outboxEvent = NewEvent();

        outboxEvent.Status.Should().Be(OutboxStatus.Pending);
        outboxEvent.Attempts.Should().Be(0);
        outboxEvent.NextAttemptAt.Should().Be(Now);
        outboxEvent.PublishedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Pending_WithNonPositiveSequence_ShouldBeRejected(long sequence)
    {
        var action = () => NewEvent(sequence);

        action.Should().Throw<ArgumentException>().WithMessage("*secuencia*");
    }

    [Fact]
    public void Pending_WithPayloadTooLarge_ShouldBeRejected()
    {
        var action = () => OutboxEvent.Pending(Guid.NewGuid(), "pago.creado", 1, OutboxEvent.AggregateTypePayment,
            Guid.NewGuid(), 1, "fixia.payments.events", "k", new string('x', OutboxEvent.MaxPayloadLength + 1), Now);

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(8, 256)]
    [InlineData(9, 300)]
    [InlineData(25, 300)]
    public void Backoff_ShouldDoubleUpToFiveMinutes(int attempts, int expectedSeconds)
    {
        OutboxEvent.Backoff(attempts).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void RegisterFailedAttempt_ShouldRescheduleWithBackoffAndTruncateError()
    {
        var outboxEvent = NewEvent();

        outboxEvent.RegisterFailedAttempt(new string('e', 800), Now);

        outboxEvent.Status.Should().Be(OutboxStatus.Pending);
        outboxEvent.Attempts.Should().Be(1);
        outboxEvent.NextAttemptAt.Should().Be(Now.AddSeconds(2));
        outboxEvent.LastError.Should().HaveLength(OutboxEvent.MaxErrorLength);
    }

    [Fact]
    public void RegisterFailedAttempt_AfterMaxAttempts_ShouldBecomeFailed_AndRequeueShouldReactivateIt()
    {
        var outboxEvent = NewEvent();

        for (var i = 0; i < OutboxEvent.MaxAttempts; i++)
            outboxEvent.RegisterFailedAttempt("Kafka no disponible", Now);

        outboxEvent.Status.Should().Be(OutboxStatus.Failed);
        FluentActions.Invoking(() => outboxEvent.MarkPublished(Now)).Should().Throw<InvalidOperationException>();

        outboxEvent.Requeue(Now.AddMinutes(1));

        outboxEvent.Status.Should().Be(OutboxStatus.Pending);
        outboxEvent.Attempts.Should().Be(0);
        outboxEvent.NextAttemptAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void MarkPublished_ShouldBeIdempotent_AndForbidLaterFailures()
    {
        var outboxEvent = NewEvent();
        outboxEvent.RegisterFailedAttempt("timeout", Now);

        outboxEvent.MarkPublished(Now.AddSeconds(3));
        outboxEvent.MarkPublished(Now.AddSeconds(9));

        outboxEvent.Status.Should().Be(OutboxStatus.Published);
        outboxEvent.PublishedAt.Should().Be(Now.AddSeconds(3));
        outboxEvent.LastError.Should().BeNull();
        FluentActions.Invoking(() => outboxEvent.RegisterFailedAttempt("x", Now)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => outboxEvent.Requeue(Now)).Should().Throw<InvalidOperationException>();
    }
}
