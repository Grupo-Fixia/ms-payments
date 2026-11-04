using System.Text.Json;
using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Messaging;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.UnitTests.Infrastructure;

public class OutboxWriterTests
{
    private static PaymentsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase($"outbox-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task EnqueueAsync_ShouldWriteEnvelopeWithTheServicesContractFormat()
    {
        // Arrange
        await using var db = NewContext();
        var writer = new OutboxWriter(db);
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 150000m, "COP", PaymentMethod.CreditCard);

        // Act
        await writer.EnqueueAsync(PaymentEvents.PaymentCreated(payment, EventActor.System));
        await db.SaveChangesAsync();

        // Assert
        var stored = await db.OutboxEvents.SingleAsync();
        stored.EventType.Should().Be("pago.creado");
        stored.Topic.Should().Be("fixia.payments.events");
        stored.MessageKey.Should().Be(payment.Id.ToString(), "la clave es aggregate.id, como exige el contrato y valida el lakehouse");
        stored.AggregateType.Should().Be("Payment");
        stored.AggregateId.Should().Be(payment.Id);
        stored.AggregateSequence.Should().Be(1);
        stored.Status.Should().Be(OutboxStatus.Pending);
        (stored.OccurredAt.Ticks % 10).Should().Be(0, "PostgreSQL guarda microsegundos");

        using var envelope = JsonDocument.Parse(stored.Payload);
        var root = envelope.RootElement;
        root.GetProperty("eventId").GetGuid().Should().Be(stored.EventId);
        root.GetProperty("eventType").GetString().Should().Be("pago.creado");
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("producer").GetString().Should().Be("ms-payments");
        root.GetProperty("occurredAt").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$");
        root.GetProperty("aggregate").GetProperty("type").GetString().Should().Be("Payment");
        root.GetProperty("aggregate").GetProperty("id").GetGuid().Should().Be(payment.Id);
        root.GetProperty("aggregate").GetProperty("sequence").GetInt64().Should().Be(1);
        root.GetProperty("correlationId").GetGuid().Should().Be(payment.ServiceRequestId);
        root.GetProperty("actor").GetProperty("role").GetString().Should().Be("SYSTEM");
        root.GetProperty("actor").GetProperty("userId").ValueKind.Should().Be(JsonValueKind.Null);

        var amount = root.GetProperty("data").GetProperty("amount");
        amount.GetProperty("amount").GetString().Should().Be("150000.00", "los importes viajan como texto");
        amount.GetProperty("currency").GetString().Should().Be("COP");
    }

    [Fact]
    public async Task EnqueueAsync_ShouldNumberEventsOfTheSamePaymentInOrder()
    {
        // Arrange
        await using var db = NewContext();
        var writer = new OutboxWriter(db);
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 150000m, "COP", PaymentMethod.CreditCard);
        var other = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 90000m, "COP", PaymentMethod.CreditCard);

        // Act: dos eventos en la misma unidad de trabajo, uno después de guardar y uno de otro pago
        await writer.EnqueueAsync(PaymentEvents.PaymentCreated(payment, EventActor.System));
        await writer.EnqueueAsync(PaymentEvents.PaymentCreated(other, EventActor.System));
        await db.SaveChangesAsync();
        payment.MarkAsCompleted("GW-1");
        await writer.EnqueueAsync(PaymentEvents.PaymentCompleted(payment, new EventActor(payment.ClientId, "CLIENT")));
        payment.Refund("REF-1");
        await writer.EnqueueAsync(PaymentEvents.PaymentRefunded(payment, new EventActor(Guid.NewGuid(), "ADMIN"), "REF-1", "prueba", PaymentEvents.RefundKinds.Gateway));
        await db.SaveChangesAsync();

        // Assert
        var sequences = await db.OutboxEvents
            .Where(e => e.AggregateId == payment.Id)
            .OrderBy(e => e.AggregateSequence)
            .Select(e => new { e.AggregateSequence, e.EventType })
            .ToListAsync();
        sequences.Select(s => s.AggregateSequence).Should().Equal(1, 2, 3);
        sequences.Select(s => s.EventType).Should().Equal("pago.creado", "pago.completado", "pago.reembolsado");

        (await db.OutboxEvents.SingleAsync(e => e.AggregateId == other.Id)).AggregateSequence.Should().Be(1);
    }

    [Fact]
    public void ToMessage_ShouldUseKeyAndHeadersOfTheContract()
    {
        var outboxEvent = OutboxEvent.Pending(Guid.NewGuid(), "pago.completado", 1, OutboxEvent.AggregateTypePayment,
            Guid.NewGuid(), 2, "fixia.payments.events", "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7", "{}",
            DateTime.UtcNow);

        var message = OutboxRelay.ToMessage(outboxEvent);

        message.Topic.Should().Be("fixia.payments.events");
        message.Key.Should().Be("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7");
        message.Value.Should().Be("{}");
        message.Headers.Should().Contain("eventId", outboxEvent.EventId.ToString())
            .And.Contain("eventType", "pago.completado")
            .And.Contain("schemaVersion", "1")
            .And.Contain("content-type", "application/json");
    }
}
