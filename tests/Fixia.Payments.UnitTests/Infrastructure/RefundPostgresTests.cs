using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;
using Fixia.Payments.Infrastructure.Gateways;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Idempotency;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Fixia.Payments.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Reverso de un pago cobrado contra PostgreSQL real (GC-487): o se hacen todos los movimientos, o ninguno. Se omiten
/// sin <c>FIXIA_PAYMENTS_TEST_PG</c>.
/// </summary>
public class RefundPostgresTests : IClassFixture<PostgresDatabase>
{
    private readonly PostgresDatabase _database;

    public RefundPostgresTests(PostgresDatabase database)
    {
        _database = database;
    }

    private void RequireDatabase() =>
        Skip.IfNot(_database.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}: se omite la prueba con PostgreSQL");

    private static readonly Requester Admin = new(Guid.NewGuid(), new[] { Requester.RoleAdmin });

    private async Task<Guid> GivenCapturedPaymentAsync(Guid clientId, Guid providerId, decimal providerWithdrew)
    {
        await using var db = _database.NewContext();
        var now = DateTime.UtcNow;
        var rule = await db.CommissionRules.SingleAsync(r => r.Id == PaymentsDbContext.InitialCommissionRuleId);
        var client = Wallet.OpenForUser(clientId, now);
        var provider = Wallet.OpenForUser(providerId, now);
        db.Wallets.AddRange(client, provider);
        var platform = await db.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId);

        var payment = Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), clientId, providerId, rule.Quote(105000m, "COP"));
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.WalletEntries.Add(client.Apply(WalletEntryType.TopUp, 120750m, now));
        db.WalletEntries.Add(client.Apply(WalletEntryType.Hold, 120750m, now, payment.Id));
        db.WalletEntries.Add(client.Apply(WalletEntryType.Capture, 120750m, now, payment.Id));
        db.WalletEntries.Add(provider.Apply(WalletEntryType.Credit, 105000m, now, payment.Id));
        db.WalletEntries.Add(platform.Apply(WalletEntryType.Commission, 15750m, now, payment.Id));
        if (providerWithdrew > 0)
            db.WalletEntries.Add(provider.Apply(WalletEntryType.Withdrawal, providerWithdrew, now));
        payment.MarkAsProcessing("HOLD-1");
        payment.MarkAsCompleted("CAPTURE-1");
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private async Task RefundAsync(Guid paymentId, string key)
    {
        await using var db = _database.NewContext();
        var service = new PaymentRefundService(new PaymentRepository(db), new WalletRepository(db),
            new MockPaymentGateway(NullLogger<MockPaymentGateway>.Instance), new OutboxWriter(db),
            new EfIdempotencyStore(db, TimeProvider.System), new UnitOfWork(db), TimeProvider.System,
            NullLogger<PaymentRefundService>.Instance);
        await service.RefundAsync(paymentId, Admin, "Disputa resuelta a favor del cliente", key);
    }

    [SkippableFact]
    public async Task Reversal_ShouldMoveEveryBalanceAndPublishOneEvent()
    {
        RequireDatabase();
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var paymentId = await GivenCapturedPaymentAsync(clientId, providerId, 0m);
        decimal platformBefore;
        await using (var db = _database.NewContext())
            platformBefore = (await db.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId)).AvailableBalance;

        await RefundAsync(paymentId, "rev-1");

        await using var check = _database.NewContext();
        (await check.Wallets.SingleAsync(w => w.OwnerId == clientId)).AvailableBalance.Should().Be(120750m);
        (await check.Wallets.SingleAsync(w => w.OwnerId == providerId)).AvailableBalance.Should().Be(0m);
        (await check.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId)).AvailableBalance
            .Should().Be(platformBefore - 15750m);
        (await check.Payments.SingleAsync(p => p.Id == paymentId)).Status.Should().Be(PaymentStatus.Refunded);
        (await check.OutboxEvents.CountAsync(e => e.AggregateId == paymentId && e.EventType == "pago.reembolsado"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Reversal_WhenTheProviderAlreadyWithdrew_ShouldRollBackEverything()
    {
        RequireDatabase();
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var paymentId = await GivenCapturedPaymentAsync(clientId, providerId, 100000m);

        var action = () => RefundAsync(paymentId, "rev-2");

        await action.Should().ThrowAsync<InsufficientBalanceException>();
        await using var check = _database.NewContext();
        (await check.Wallets.SingleAsync(w => w.OwnerId == clientId)).AvailableBalance.Should().Be(0m);
        (await check.Wallets.SingleAsync(w => w.OwnerId == providerId)).AvailableBalance.Should().Be(5000m);
        (await check.Payments.SingleAsync(p => p.Id == paymentId)).Status.Should().Be(PaymentStatus.Completed);
        (await check.OutboxEvents.CountAsync(e => e.AggregateId == paymentId)).Should().Be(0);
        (await check.IdempotencyRecords.CountAsync(r => r.Key == "rev-2")).Should().Be(0, "la clave queda libre");
    }
}
