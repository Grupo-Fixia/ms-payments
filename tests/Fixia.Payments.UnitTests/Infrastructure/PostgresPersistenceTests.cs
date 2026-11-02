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
/// Pruebas contra PostgreSQL real (GC-477). Se omiten si no está la variable <c>FIXIA_PAYMENTS_TEST_PG</c>; ver
/// <see cref="PostgresDatabase"/>.
/// </summary>
public class PostgresPersistenceTests : IClassFixture<PostgresDatabase>
{
    private readonly PostgresDatabase _database;

    public PostgresPersistenceTests(PostgresDatabase database)
    {
        _database = database;
    }

    private void RequireDatabase() =>
        Skip.IfNot(_database.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}: se omite la prueba con PostgreSQL");

    private PaymentService NewService(PaymentsDbContext db) => new(
        new PaymentRepository(db),
        new UnitOfWork(db),
        new MockPaymentGateway(NullLogger<MockPaymentGateway>.Instance),
        new OutboxWriter(db),
        NullLogger<PaymentService>.Instance);

    private static Payment NewPayment() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 105000m, "COP", PaymentMethod.CreditCard);

    [SkippableFact]
    public async Task Migrations_OnEmptyDatabase_ShouldCreateTheSchemaAndBeRepeatable()
    {
        RequireDatabase();
        await using var db = _database.NewContext();

        (await db.Database.GetAppliedMigrationsAsync()).Should().ContainSingle(m => m.EndsWith("_InitialCreate"));
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        await db.Database.MigrateAsync(); // aplicar de nuevo no cambia nada

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name",
            connection);
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));

        tables.Should().Contain(new[] { "payments", "payment_transactions", "outbox_events", "__EFMigrationsHistory" });
    }

    [SkippableFact]
    public async Task Payments_ShouldAllowOnlyOnePaymentPerServiceRequest()
    {
        RequireDatabase();
        var first = NewPayment();
        var duplicate = new Payment(first.ServiceRequestId, Guid.NewGuid(), Guid.NewGuid(), 1m, "COP", PaymentMethod.Wallet);

        await using (var db = _database.NewContext())
        {
            db.Payments.Add(first);
            await db.SaveChangesAsync();
        }

        await using var other = _database.NewContext();
        other.Payments.Add(duplicate);
        var action = () => other.SaveChangesAsync();

        (await action.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [SkippableFact]
    public async Task Payments_ConcurrentChanges_ShouldLetOnlyOneWin()
    {
        RequireDatabase();
        var payment = NewPayment();
        await using (var db = _database.NewContext())
        {
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
        }

        await using var first = _database.NewContext();
        await using var second = _database.NewContext();
        var copyA = await first.Payments.SingleAsync(p => p.Id == payment.Id);
        var copyB = await second.Payments.SingleAsync(p => p.Id == payment.Id);

        copyA.MarkAsProcessing("TX-A");
        await first.SaveChangesAsync();
        copyB.MarkAsProcessing("TX-B");
        var action = () => second.SaveChangesAsync();

        await action.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [SkippableFact]
    public async Task Service_ShouldWritePaymentAndEventInTheSameTransaction()
    {
        RequireDatabase();
        var request = new CreatePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 150000m, "COP", PaymentMethod.CreditCard);
        PaymentResponse created;

        await using (var db = _database.NewContext())
            created = (await NewService(db).CreatePaymentAsync(request)).Payment;
        await using (var db = _database.NewContext())
            await NewService(db).ProcessPaymentAsync(created.Id, new Requester(request.ClientId, new[] { Requester.RoleClient }));

        await using var check = _database.NewContext();
        var events = await check.OutboxEvents
            .Where(e => e.AggregateId == created.Id)
            .OrderBy(e => e.AggregateSequence)
            .ToListAsync();
        events.Select(e => e.EventType).Should().Equal(PaymentEvents.Created, PaymentEvents.Completed);
        events.Should().OnlyContain(e => e.Status == OutboxStatus.Pending && e.MessageKey == request.ServiceRequestId.ToString());

        var stored = await check.Payments.SingleAsync(p => p.Id == created.Id);
        stored.Status.Should().Be(PaymentStatus.Completed);
        events[1].Payload.Should().Contain(stored.UpdatedAt!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"),
            "el instante del sobre es el mismo que quedó guardado");
    }
}
