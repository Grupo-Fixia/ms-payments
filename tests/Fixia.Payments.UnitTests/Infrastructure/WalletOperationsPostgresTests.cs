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
            TimeProvider.System,
            new WithdrawalLimits());
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
    public async Task DifferentKeysAtTheSameTime_ShouldAllApplyWithoutLosingOrInventingMoney()
    {
        // GC-501: antes 6 a 8 de 10 respondían 409; ahora el servicio reintenta y todas se aplican.
        RequireDatabase();
        var userId = Guid.NewGuid();
        await WithServiceAsync(s => s.TopUpAsync(Client(userId), "calentar", new WalletAmountRequest(1m)));

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            WithServiceAsync(s => s.TopUpAsync(Client(userId), $"k-{i}", new WalletAmountRequest(10000m))))));

        results.Should().OnlyContain(r => !r.Replayed);
        var state = await StateAsync(userId);
        state.Available.Should().Be(1m + 10000m * 10);
        state.Entries.Should().Be(11);
        state.Keys.Should().Be(11);
    }

    [SkippableFact]
    public async Task WithdrawalsAtTheSameTime_ShouldNeverOverdrawNorPassTheDailyLimit()
    {
        RequireDatabase();
        var userId = Guid.NewGuid();
        await using (var db = _database.NewContext())
        {
            var wallet = Fixia.Payments.Domain.Entities.Wallet.OpenForUser(userId, DateTime.UtcNow);
            db.Wallets.Add(wallet);
            db.WalletEntries.Add(wallet.Apply(Fixia.Payments.Domain.Enums.WalletEntryType.Credit, 10000m,
                DateTime.UtcNow));
            await db.SaveChangesAsync();
        }
        var professional = new Requester(userId, new[] { Requester.RoleProfessional });

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            try
            {
                await WithServiceAsync(s => s.WithdrawAsync(professional, $"r-{i}", new WalletAmountRequest(100m)));
                return true;
            }
            catch (WithdrawalLimitExceededException)
            {
                return false;
            }
        })));

        outcomes.Count(ok => ok).Should().Be(3, "el tope diario es 3 aunque lleguen todos a la vez");
        (await StateAsync(userId)).Available.Should().Be(9700m);
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
