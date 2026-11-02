using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Gateways;
using Fixia.Payments.Infrastructure.Messaging;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Fixia.Payments.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Relay del Outbox contra PostgreSQL real (GC-477), con su propia base para que no publique eventos de otras pruebas.
/// Se omite sin <c>FIXIA_PAYMENTS_TEST_PG</c>; ver <see cref="PostgresDatabase"/>.
/// </summary>
public class OutboxRelayPostgresTests : IClassFixture<PostgresDatabase>
{
    private readonly PostgresDatabase _database;

    public OutboxRelayPostgresTests(PostgresDatabase database)
    {
        _database = database;
    }

    private void RequireDatabase() =>
        Skip.IfNot(_database.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}: se omite la prueba con PostgreSQL");

    private static Payment NewPayment() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 105000m, "COP", PaymentMethod.CreditCard);

    [SkippableFact]
    public async Task Relay_ShouldPublishInOrder_AndRecoverAfterKafkaFails()
    {
        RequireDatabase();
        var clock = new ManualClock(DateTime.UtcNow.AddMinutes(1));
        var broker = new FakeBroker();
        var relay = NewRelay(broker, clock);

        Guid paymentId;
        await using (var db = _database.NewContext())
        {
            var payment = NewPayment();
            db.Payments.Add(payment);
            var writer = new OutboxWriter(db);
            await writer.EnqueueAsync(PaymentEvents.PaymentCreated(payment, EventActor.System));
            payment.MarkAsCompleted("GW-1");
            await writer.EnqueueAsync(PaymentEvents.PaymentCompleted(payment, EventActor.System));
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        // 1. Kafka caído: el primer evento falla y el segundo no sale antes que él.
        broker.Available = false;
        (await relay.PublishPendingAsync(CancellationToken.None)).Should().Be(0);

        await using (var db = _database.NewContext())
        {
            var events = await db.OutboxEvents.Where(e => e.AggregateId == paymentId)
                .OrderBy(e => e.AggregateSequence).ToListAsync();
            events[0].Status.Should().Be(OutboxStatus.Pending);
            events[0].Attempts.Should().Be(1);
            events[0].LastError.Should().Contain("Kafka no disponible");
            events[0].NextAttemptAt.Should().BeCloseTo(clock.Now.AddSeconds(2), TimeSpan.FromMilliseconds(1));
            events[1].Attempts.Should().Be(0, "la secuencia 2 espera a la 1");
        }

        // 2. Antes de que venza la espera no se reintenta.
        broker.Available = true;
        (await relay.PublishPendingAsync(CancellationToken.None)).Should().Be(0);

        // 3. Kafka vuelve y vence la espera: sale la 1 y, en la ronda siguiente, la 2.
        clock.Advance(TimeSpan.FromSeconds(3));
        (await relay.PublishPendingAsync(CancellationToken.None)).Should().Be(1);
        (await relay.PublishPendingAsync(CancellationToken.None)).Should().Be(1);
        (await relay.PublishPendingAsync(CancellationToken.None)).Should().Be(0);

        var published = broker.Messages.Where(m => m.Headers["eventType"].StartsWith("pago.")).ToList();
        published.Select(m => m.Headers["eventType"]).Should().Equal(PaymentEvents.Created, PaymentEvents.Completed);
        published.Should().OnlyContain(m => m.Topic == PaymentEvents.Topic);

        await using var check = _database.NewContext();
        (await check.OutboxEvents.Where(e => e.AggregateId == paymentId).ToListAsync())
            .Should().OnlyContain(e => e.Status == OutboxStatus.Published && e.PublishedAt != null);
    }

    private OutboxRelay NewRelay(IMessagePublisher broker, TimeProvider clock)
    {
        var services = new ServiceCollection()
            .AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(_database.ConnectionString))
            .BuildServiceProvider();
        return new OutboxRelay(services.GetRequiredService<IServiceScopeFactory>(), broker, clock,
            Options.Create(new OutboxOptions()), NullLogger<OutboxRelay>.Instance);
    }

    private sealed class FakeBroker : IMessagePublisher
    {
        public bool Available { get; set; } = true;
        public List<BrokerMessage> Messages { get; } = new();

        public Task PublishAsync(BrokerMessage message, CancellationToken ct = default)
        {
            if (!Available)
                throw new InvalidOperationException("Kafka no disponible");
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public ManualClock(DateTime start) => Now = DateTime.SpecifyKind(start, DateTimeKind.Utc);

        public DateTime Now { get; private set; }

        public void Advance(TimeSpan by) => Now += by;

        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
