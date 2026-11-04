using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Exceptions;
using Fixia.Payments.Infrastructure.Gateways;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Idempotency;
using Fixia.Payments.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Recarga y retiro con Idempotency-Key contra PostgreSQL real (GC-482, GC-483): transacciones, rollback y peticiones
/// simultáneas. Se omiten sin <c>FIXIA_PAYMENTS_TEST_PG</c>.
/// </summary>
public class WalletOperationsPostgresTests : IClassFixture<PostgresDatabase>
{
    private readonly PostgresDatabase _database;

    public WalletOperationsPostgresTests(PostgresDatabase database)
    {
        _database = database;
    }

    private void RequireDatabase() =>
        Skip.IfNot(_database.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}: se omite la prueba con PostgreSQL");

    private static Requester Client(Guid id) => new(id, new[] { Requester.RoleClient });

    /// <summary>Cada llamada con su propio contexto, como cada petición HTTP.</summary>
    private async Task<T> WithServiceAsync<T>(Func<WalletOperationsService, Task<T>> call)
    {
        await using var db = _database.NewContext();
        var service = new WalletOperationsService(
            new WalletRepository(db),
            new MockPaymentGateway(NullLogger<MockPaymentGateway>.Instance),
            new EfIdempotencyStore(db, TimeProvider.System),
            new UnitOfWork(db),
            TimeProvider.System);
        return await call(service);
    }

    private async Task<(decimal Available, int Entries, int Keys)> StateAsync(Guid userId)
    {
        await using var db = _database.NewContext();
        var wallet = await db.Wallets.SingleAsync(w => w.OwnerId == userId);
        return (wallet.AvailableBalance,
            await db.WalletEntries.CountAsync(e => e.WalletId == wallet.Id),
            await db.IdempotencyRecords.CountAsync(r => r.UserId == userId));
    }

    [SkippableFact]
    public async Task SameKeyAtTheSameTime_ShouldTopUpOnlyOnce()
    {
        RequireDatabase();
        var userId = Guid.NewGuid();
        await WithServiceAsync(s => s.TopUpAsync(Client(userId), "calentar", new WalletAmountRequest(1m)));
        var key = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            WithServiceAsync(s => s.TopUpAsync(Client(userId), key, new WalletAmountRequest(50000m))))));

        results.Select(r => r.Value.EntryId).Distinct().Should().ContainSingle();
        results.Count(r => !r.Replayed).Should().Be(1);
        var state = await StateAsync(userId);
        state.Available.Should().Be(50001m);
        state.Entries.Should().Be(2);
    }

    [SkippableFact]
    public async Task DifferentKeysAtTheSameTime_ShouldNeverLoseOrInventMoney()
    {
        RequireDatabase();
        var userId = Guid.NewGuid();
        await WithServiceAsync(s => s.TopUpAsync(Client(userId), "calentar", new WalletAmountRequest(1m)));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await WithServiceAsync(s => s.TopUpAsync(Client(userId), $"k-{i}", new WalletAmountRequest(1000m)));
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false; // 409: el cliente reintenta con la misma clave
            }
        })));

        var succeeded = outcomes.Count(ok => ok);
        succeeded.Should().BeGreaterThan(0);
        var state = await StateAsync(userId);
        state.Available.Should().Be(1m + 1000m * succeeded);
        state.Entries.Should().Be(1 + succeeded);
        state.Keys.Should().Be(1 + succeeded, "las que chocaron no dejan clave guardada y se pueden reintentar");
    }

    [SkippableFact]
    public async Task RejectedTopUp_ShouldLeaveNoKey_SoTheSameKeyCanBeRetried()
    {
        RequireDatabase();
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var rejected = () => WithServiceAsync(s => s.TopUpAsync(Client(userId), key, new WalletAmountRequest(10_000_000.01m)));
        await rejected.Should().ThrowAsync<GatewayRejectedException>();
        await rejected.Should().ThrowAsync<GatewayRejectedException>("la clave no quedó tomada por el intento rechazado");

        var state = await StateAsync(userId);
        state.Available.Should().Be(0m);
        state.Entries.Should().Be(0);
        state.Keys.Should().Be(0);
    }

    [SkippableFact]
    public async Task WithdrawalWithoutEnoughBalance_ShouldRollBackEverything()
    {
        RequireDatabase();
        var userId = Guid.NewGuid();
        var professional = new Requester(userId, new[] { Requester.RoleProfessional });

        var action = () => WithServiceAsync(s => s.WithdrawAsync(professional, "r-1", new WalletAmountRequest(10m)));

        await action.Should().ThrowAsync<InsufficientBalanceException>();
        var state = await StateAsync(userId);
        state.Available.Should().Be(0m);
        state.Entries.Should().Be(0);
        state.Keys.Should().Be(0);
    }
}
