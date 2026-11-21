using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;
using Fixia.Payments.Infrastructure.Gateways;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Audit;
using Fixia.Payments.Infrastructure.Persistence.Idempotency;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Fixia.Payments.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Registro de acciones del ADMIN y lista de pagos contra PostgreSQL real (GC-535): la migración, la traducción a SQL
/// de los filtros y del cursor, y que el registro de un reembolso se confirme o se revierta con el reembolso. Se omiten
/// sin <c>FIXIA_PAYMENTS_TEST_PG</c>.
/// </summary>
public class AdminAuditPostgresTests : IClassFixture<PostgresDatabase>
{
    private readonly PostgresDatabase _database;

    public AdminAuditPostgresTests(PostgresDatabase database)
    {
        _database = database;
    }

    private void RequireDatabase() =>
        Skip.IfNot(_database.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}: se omite la prueba con PostgreSQL");

    /// <summary>Contenedor mínimo: <see cref="EfAdminAuditLog"/> abre su propio contexto para lo que se guarda aparte.</summary>
    private ServiceProvider NewServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(_database.ConnectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAdminAuditLog, EfAdminAuditLog>();
        return services.BuildServiceProvider();
    }

    private async Task<Payment> GivenPaymentAsync(Guid clientId, Guid providerId, bool captured, decimal providerWithdrew = 0m)
    {
        await using var db = _database.NewContext();
        var now = DateTime.UtcNow;
        var rule = await db.CommissionRules.SingleAsync(r => r.Id == PaymentsDbContext.InitialCommissionRuleId);
        var client = await db.Wallets.SingleOrDefaultAsync(w => w.OwnerId == clientId)
                     ?? db.Wallets.Add(Wallet.OpenForUser(clientId, now)).Entity;
        var provider = await db.Wallets.SingleOrDefaultAsync(w => w.OwnerId == providerId)
                       ?? db.Wallets.Add(Wallet.OpenForUser(providerId, now)).Entity;
        var platform = await db.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId);

        var payment = Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), clientId, providerId, rule.Quote(105000m, "COP"));
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.WalletEntries.Add(client.Apply(WalletEntryType.TopUp, 120750m, now));
        db.WalletEntries.Add(client.Apply(WalletEntryType.Hold, 120750m, now, payment.Id));
        payment.MarkAsProcessing("HOLD-1");
        if (captured)
        {
            db.WalletEntries.Add(client.Apply(WalletEntryType.Capture, 120750m, now, payment.Id));
            db.WalletEntries.Add(provider.Apply(WalletEntryType.Credit, 105000m, now, payment.Id));
            db.WalletEntries.Add(platform.Apply(WalletEntryType.Commission, 15750m, now, payment.Id));
            if (providerWithdrew > 0)
                db.WalletEntries.Add(provider.Apply(WalletEntryType.Withdrawal, providerWithdrew, now));
            payment.MarkAsCompleted("CAPTURE-1");
        }

        await db.SaveChangesAsync();
        return payment;
    }

    private static async Task RefundAsync(IServiceProvider services, Guid adminId, Guid paymentId, string key)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var service = new PaymentRefundService(new PaymentRepository(db), new WalletRepository(db),
            new MockPaymentGateway(NullLogger<MockPaymentGateway>.Instance), new OutboxWriter(db),
            new EfIdempotencyStore(db, TimeProvider.System), new UnitOfWork(db), TimeProvider.System,
            NullLogger<PaymentRefundService>.Instance, scope.ServiceProvider.GetRequiredService<IAdminAuditLog>());
        await service.RefundAsync(paymentId, new Requester(adminId, new[] { Requester.RoleAdmin }),
            "Objeción resuelta a favor del cliente", key);
    }

    private async Task<List<AdminAuditEvent>> AuditOfAsync(Guid adminId)
    {
        await using var db = _database.NewContext();
        return await db.AdminAuditEvents.Where(e => e.AdminId == adminId).OrderBy(e => e.OccurredAt).ToListAsync();
    }

    [SkippableFact]
    public async Task SuccessfulRefund_ShouldBeRecordedInTheSameTransaction()
    {
        RequireDatabase();
        await using var services = NewServices();
        var adminId = Guid.NewGuid();
        var payment = await GivenPaymentAsync(Guid.NewGuid(), Guid.NewGuid(), captured: false);

        await RefundAsync(services, adminId, payment.Id, "audit-ok");

        var events = await AuditOfAsync(adminId);
        events.Should().ContainSingle();
        events[0].Action.Should().Be(AdminAction.PAYMENT_REFUND);
        events[0].TargetType.Should().Be("PAYMENT");
        events[0].TargetId.Should().Be(payment.Id);
        events[0].Outcome.Should().Be(AdminAuditOutcome.OK);
        events[0].Details.Should().Contain("\"kind\":\"RELEASE\"").And.Contain("audit-ok");
    }

    [SkippableFact]
    public async Task FailedRefund_ShouldRollBackTheMoneyButKeepAConflictRecord()
    {
        RequireDatabase();
        await using var services = NewServices();
        var adminId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var payment = await GivenPaymentAsync(Guid.NewGuid(), providerId, captured: true, providerWithdrew: 100000m);

        var action = () => RefundAsync(services, adminId, payment.Id, "audit-conflict");

        await action.Should().ThrowAsync<InsufficientBalanceException>();
        await using (var check = _database.NewContext())
        {
            (await check.Wallets.SingleAsync(w => w.OwnerId == providerId)).AvailableBalance.Should().Be(5000m);
            (await check.Payments.SingleAsync(p => p.Id == payment.Id)).Status.Should().Be(PaymentStatus.Completed);
        }

        var events = await AuditOfAsync(adminId);
        events.Should().ContainSingle("el registro OK se revirtió con el reembolso; solo queda el intento fallido");
        events[0].Outcome.Should().Be(AdminAuditOutcome.CONFLICT);
        events[0].Details.Should().NotContain("kind");
    }

    [SkippableFact]
    public async Task AdminList_ShouldTranslateFiltersAndCursorToSql()
    {
        RequireDatabase();
        var clientId = Guid.NewGuid();
        var held = await GivenPaymentAsync(clientId, Guid.NewGuid(), captured: false);
        var captured = await GivenPaymentAsync(clientId, Guid.NewGuid(), captured: true);
        await using var services = NewServices();
        await RefundAsync(services, Guid.NewGuid(), held.Id, "release-1"); // queda Released
        var other = await GivenPaymentAsync(clientId, Guid.NewGuid(), captured: false);

        await using var db = _database.NewContext();
        var repository = new PaymentRepository(db);
        async Task<List<Guid>> Ids(WalletHoldStatus? hold, PaymentStatus? status = null) =>
            (await repository.ListForAdminAsync(new AdminPaymentFilter(status, hold, null, clientId), null, 10))
            .Select(p => p.Id).ToList();

        (await Ids(WalletHoldStatus.Released)).Should().Equal(held.Id);
        (await Ids(WalletHoldStatus.Reversed)).Should().BeEmpty();
        (await Ids(WalletHoldStatus.Captured)).Should().Equal(captured.Id);
        (await Ids(WalletHoldStatus.Held)).Should().Equal(other.Id);
        (await Ids(null, PaymentStatus.Refunded)).Should().Equal(held.Id);

        var first = await repository.ListForAdminAsync(new AdminPaymentFilter(null, null, null, clientId), null, 2);
        var last = first[^1];
        var rest = await repository.ListForAdminAsync(new AdminPaymentFilter(null, null, null, clientId),
            new AdminListPosition(last.CreatedAt, last.Id), 10);
        first.Concat(rest).Select(p => p.Id).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(new[] { held.Id, captured.Id, other.Id });
    }

    [SkippableFact]
    public async Task AuditList_ShouldPageNewestFirst()
    {
        RequireDatabase();
        await using var services = NewServices();
        var adminId = Guid.NewGuid();
        using var scope = services.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
        for (var i = 0; i < 3; i++)
            await log.RecordAsync(adminId, AdminAction.PAYMENT_LIST, null, new { limit = i + 1 }, AdminAuditOutcome.OK);

        var first = await log.ListAsync(adminId, AdminAction.PAYMENT_LIST, null, 2);
        var rest = await log.ListAsync(adminId, null, new AdminListPosition(first[^1].OccurredAt, first[^1].Id), 10);

        first.Select(e => e.Details).Should().Equal("{\"limit\":3}", "{\"limit\":2}");
        rest.Select(e => e.Details).Should().Equal("{\"limit\":1}");
    }
}
