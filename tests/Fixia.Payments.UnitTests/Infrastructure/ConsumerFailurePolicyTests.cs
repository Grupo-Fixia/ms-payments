using Confluent.Kafka;
using Fixia.Payments.Domain.Exceptions;
using Fixia.Payments.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Un mensaje que falla siempre no puede bloquear la partición (incidente del 2026-11-24), pero un fallo transitorio
/// nunca debe hacer que se salte un cobro.
/// </summary>
public class ConsumerFailurePolicyTests
{
    private static readonly TopicPartitionOffset Message =
        new("fixia.services.service-requests", new Partition(1), new Offset(11));

    [Fact]
    public void BusinessError_ShouldRetryAndThenPark()
    {
        var policy = new ConsumerFailurePolicy(maxAttempts: 3);
        var error = new InvalidOperationException("El pago está retenido pero el cliente no tiene billetera");

        policy.OnFailure(Message, error).Should().Be((ConsumerFailureAction.Retry, 1));
        policy.OnFailure(Message, error).Should().Be((ConsumerFailureAction.Retry, 2));
        policy.OnFailure(Message, error).Should().Be((ConsumerFailureAction.Park, 3));
    }

    [Fact]
    public void InsufficientHeldBalance_ShouldEventuallyPark()
    {
        var policy = new ConsumerFailurePolicy(maxAttempts: 1);

        policy.OnFailure(Message, new InsufficientBalanceException("Saldo retenido insuficiente")).Action
            .Should().Be(ConsumerFailureAction.Park);
    }

    [Fact]
    public void TransientErrors_ShouldAlwaysRetry()
    {
        var policy = new ConsumerFailurePolicy(maxAttempts: 1);
        var transient = new Exception[]
        {
            new DbUpdateConcurrencyException("xmin cambió"),
            new TimeoutException(),
            new NpgsqlException("connection refused", new System.Net.Sockets.SocketException()),
            new InvalidOperationException("transient failure", new TimeoutException()),
            new KafkaException(ErrorCode.Local_Transport)
        };

        foreach (var error in transient)
        {
            for (var i = 0; i < 5; i++)
                policy.OnFailure(Message, error).Action.Should().Be(ConsumerFailureAction.Retry, error.GetType().Name);
        }
    }

    [Fact]
    public void ConstraintViolation_ShouldNotBeTransient()
    {
        var violation = new DbUpdateException("check", new PostgresException("viola ck_wallets_held_non_negative",
            "ERROR", "ERROR", PostgresErrorCodes.CheckViolation));

        ConsumerFailurePolicy.IsTransient(violation).Should().BeFalse();
    }

    [Fact]
    public void OnDone_ShouldResetTheCount()
    {
        var policy = new ConsumerFailurePolicy(maxAttempts: 2);
        var error = new InvalidOperationException("x");

        policy.OnFailure(Message, error);
        policy.OnDone(Message);

        policy.OnFailure(Message, error).Should().Be((ConsumerFailureAction.Retry, 1));
    }

    [Fact]
    public void EachMessage_ShouldCountItsOwnAttempts()
    {
        var policy = new ConsumerFailurePolicy(maxAttempts: 2);
        var error = new InvalidOperationException("x");
        var other = new TopicPartitionOffset(Message.Topic, Message.Partition, new Offset(12));

        policy.OnFailure(Message, error);

        policy.OnFailure(other, error).Should().Be((ConsumerFailureAction.Retry, 1));
        policy.OnFailure(Message, error).Should().Be((ConsumerFailureAction.Park, 2));
    }
}
