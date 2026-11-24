using System.Text.Json;
using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Inbox;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Fixia.Payments.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Retener al contratar y cobrar al cerrar contra PostgreSQL real (GC-484, GC-485): el flujo que hace el consumidor de
/// <c>fixia.services.service-requests</c>, cada evento con su propio contexto. Se omiten sin
/// <c>FIXIA_PAYMENTS_TEST_PG</c>.
/// </summary>
public class ServiceEventsPostgresTests : IClassFixture<PostgresDatabase>
{
    private readonly PostgresDatabase _database;
    private readonly Guid _serviceRequestId = Guid.NewGuid();
    private readonly Guid _contractId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _providerId = Guid.NewGuid();

    public ServiceEventsPostgresTests(PostgresDatabase database)
    {
        _database = database;
    }

    private void RequireDatabase() =>
        Skip.IfNot(_database.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}: se omite la prueba con PostgreSQL");

    private async Task<bool> HandleAsync(ServiceEvent serviceEvent)
    {
        await using var db = _database.NewContext();
        var handler = new ServiceEventsHandler(
            new PaymentRepository(db),
            new WalletRepository(db),
            new CommissionService(new CommissionRuleRepository(db), TimeProvider.System),
            new OutboxWriter(db),
            new EfProcessedEventStore(db),
            new UnitOfWork(db),
            TimeProvider.System,
            NullLogger<ServiceEventsHandler>.Instance);
        return await handler.HandleAsync(serviceEvent);
    }

    private ServiceEvent Event(string type, object data) =>
        ServiceEvent.Parse(JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = type,
            schemaVersion = 1,
            occurredAt = "2026-11-02T17:45:00Z",
            producer = "ms-services",
            aggregate = new { type = "ServiceRequest", id = _serviceRequestId, sequence = 8 },
            correlationId = _serviceRequestId,
            actor = new { userId = _clientId, role = "CLIENT" },
            data
        }));

    private ServiceEvent Contracted() => Event(ServiceEvent.Contracted, new
    {
        contractId = _contractId,
        serviceRequestId = _serviceRequestId,
        quotationId = Guid.NewGuid(),
        clientId = _clientId,
        providerId = _providerId,
        price = new { amount = "105000.00", currency = "COP" },
        scope = "Cambio de la tubería",
        conditions = "Materiales incluidos",
        agreedAt = "2026-11-03T15:00:00Z",
        contractedAt = "2026-11-02T17:45:00Z",
        status = "CONTRACTED"
    });

    private ServiceEvent Closed() => Event(ServiceEvent.Closed, new
    {
        contractId = _contractId,
        serviceRequestId = _serviceRequestId,
        clientId = _clientId,
        providerId = _providerId,
        closedAt = "2026-11-04T10:00:00Z",
        status = "CLOSED"
    });

    private async Task GivenClientBalanceAsync(decimal amount)
    {
        await using var db = _database.NewContext();
        var wallet = Wallet.OpenForUser(_clientId, DateTime.UtcNow);
        db.Wallets.Add(wallet);
        db.WalletEntries.Add(wallet.Apply(WalletEntryType.TopUp, amount, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task ContractedThenClosed_ShouldHoldAndThenCaptureTheMoney()
    {
        RequireDatabase();
        await GivenClientBalanceAsync(200000m);
        decimal platformBefore;
        await using (var db = _database.NewContext())
            platformBefore = (await db.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId)).AvailableBalance;

        (await HandleAsync(Contracted())).Should().BeTrue();
        (await HandleAsync(Closed())).Should().BeTrue();

        await using var check = _database.NewContext();
        var payment = await check.Payments.Include(p => p.Transactions)
            .SingleAsync(p => p.ServiceRequestId == _serviceRequestId);
        payment.Status.Should().Be(PaymentStatus.Completed);
        payment.Transactions.Should().ContainSingle(t => t.TransactionType == "CHARGE");
        var client = await check.Wallets.SingleAsync(w => w.OwnerId == _clientId);
        client.AvailableBalance.Should().Be(200000m - 120750m);
        client.HeldBalance.Should().Be(0m);
        (await check.Wallets.SingleAsync(w => w.OwnerId == _providerId)).AvailableBalance.Should().Be(105000m);
        (await check.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId)).AvailableBalance
            .Should().Be(platformBefore + 15750m);
        (await check.OutboxEvents.CountAsync(e => e.AggregateId == payment.Id && e.EventType == "pago.completado"))
            .Should().Be(1);
    }

    /// <summary>
    /// Incidente del 2026-11-24: si la cuenta de Fixia no está (tablas vaciadas sin volver a migrar), el cierre la
    /// recrea y cobra, en lugar de fallar en cada reintento y bloquear la partición del consumidor.
    /// </summary>
    [SkippableFact]
    public async Task Closed_WhenThePlatformWalletWasDeleted_ShouldRecreateItAndCapture()
    {
        RequireDatabase();
        await GivenClientBalanceAsync(200000m);
        (await HandleAsync(Contracted())).Should().BeTrue();
        await using (var db = _database.NewContext())
        {
            // Se borra la cuenta de Fixia junto con sus movimientos, como quedaría tras vaciar las tablas.
            var platform = await db.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId);
            db.WalletEntries.RemoveRange(db.WalletEntries.Where(e => e.WalletId == platform.Id));
            db.Wallets.Remove(platform);
            await db.SaveChangesAsync();
        }

        (await HandleAsync(Closed())).Should().BeTrue();

        await using var check = _database.NewContext();
        (await check.Payments.SingleAsync(p => p.ServiceRequestId == _serviceRequestId)).Status
            .Should().Be(PaymentStatus.Completed);
        var recreated = await check.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId);
        recreated.Id.Should().Be(PaymentsDbContext.PlatformWalletId);
        recreated.OwnerType.Should().Be(WalletOwnerType.Platform);
        recreated.AvailableBalance.Should().Be(15750m);
    }
}
