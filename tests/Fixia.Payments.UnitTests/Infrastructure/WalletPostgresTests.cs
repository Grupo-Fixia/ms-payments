using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Billeteras, libro y tarifas contra PostgreSQL real (GC-480, GC-481). Se omiten sin <c>FIXIA_PAYMENTS_TEST_PG</c>.
/// </summary>
public class WalletPostgresTests : IClassFixture<PostgresDatabase>
{
    private readonly PostgresDatabase _database;

    public WalletPostgresTests(PostgresDatabase database)
    {
        _database = database;
    }

    private void RequireDatabase() =>
        Skip.IfNot(_database.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}: se omite la prueba con PostgreSQL");

    private async Task<PostgresException> SqlFailsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var action = () => command.ExecuteNonQueryAsync();
        return (await action.Should().ThrowAsync<PostgresException>()).Which;
    }

    [SkippableFact]
    public async Task Migration_ShouldSeedThePlatformWalletAndTheInitialRule()
    {
        RequireDatabase();
        await using var db = _database.NewContext();

        var platform = await db.Wallets.SingleAsync(w => w.OwnerType == WalletOwnerType.Platform);
        platform.Id.Should().Be(PaymentsDbContext.PlatformWalletId);
        platform.OwnerId.Should().Be(Wallet.PlatformOwnerId);
        platform.AvailableBalance.Should().Be(0m);

        var rule = await new CommissionRuleRepository(db).GetValidAtAsync(DateTime.UtcNow);
        rule!.Id.Should().Be(PaymentsDbContext.InitialCommissionRuleId);
        rule.Rate.Should().Be(0.15m);
        rule.Quote(105000m, "COP").Total.Should().Be(120750m);
    }

    [SkippableFact]
    public async Task Database_ShouldRejectNegativeBalancesAndInconsistentBreakdowns()
    {
        RequireDatabase();

        (await SqlFailsAsync($"UPDATE wallets SET available_balance = -1 WHERE id = '{PaymentsDbContext.PlatformWalletId}'"))
            .SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        (await SqlFailsAsync($"UPDATE wallets SET held_balance = -0.01 WHERE id = '{PaymentsDbContext.PlatformWalletId}'"))
            .SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        (await SqlFailsAsync(@"INSERT INTO payments (id, service_request_id, client_id, provider_id, amount, currency,
                status, method, created_at, base_amount, commission_amount)
            VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 100, 'COP',
                'Pending', 'Wallet', now(), 90, 5)"))
            .SqlState.Should().Be(PostgresErrorCodes.CheckViolation, "100 no es 90 + 5");
        (await SqlFailsAsync(@"INSERT INTO commission_rules (id, rate, valid_from, description, created_at)
            VALUES (gen_random_uuid(), 1.5, now(), 'mala', now())"))
            .SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [SkippableFact]
    public async Task GetOrCreate_CalledAtTheSameTime_ShouldCreateASingleWallet()
    {
        RequireDatabase();
        var userId = Guid.NewGuid();

        async Task<Guid> OpenAsync()
        {
            await using var db = _database.NewContext();
            return (await new WalletRepository(db).GetOrCreateForUserAsync(userId, "COP", DateTime.UtcNow)).Id;
        }

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(OpenAsync)));

        ids.Distinct().Should().ContainSingle();
        await using var check = _database.NewContext();
        (await check.Wallets.CountAsync(w => w.OwnerId == userId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task ListEntries_WithTiesInTheSameInstant_ShouldPageWithoutRepeatingOrSkipping()
    {
        RequireDatabase();
        var userId = Guid.NewGuid();
        var sameInstant = new DateTime(2026, 11, 2, 21, 0, 0, DateTimeKind.Utc);
        Guid walletId;
        await using (var db = _database.NewContext())
        {
            var wallet = await new WalletRepository(db).GetOrCreateForUserAsync(userId, "COP", DateTime.UtcNow);
            for (var i = 1; i <= 7; i++)
                db.WalletEntries.Add(wallet.Apply(WalletEntryType.TopUp, i, i <= 5 ? sameInstant : sameInstant.AddMinutes(i)));
            await db.SaveChangesAsync();
            walletId = wallet.Id;
        }

        await using var read = _database.NewContext();
        var repository = new WalletRepository(read);
        var seen = new List<WalletEntry>();
        WalletEntryPosition? after = null;
        while (true)
        {
            var page = await repository.ListEntriesAsync(walletId, after, 2);
            if (page.Count == 0)
                break;
            seen.AddRange(page);
            after = new WalletEntryPosition(page[^1].CreatedAt, page[^1].Id);
        }

        seen.Should().HaveCount(7);
        seen.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        seen.Select(e => e.CreatedAt).Should().BeInDescendingOrder();
        seen[0].AvailableAfter.Should().Be(28m, "1 + 2 + … + 7");
    }

    [SkippableFact]
    public async Task Wallet_ConcurrentChanges_ShouldLetOnlyOneWin()
    {
        RequireDatabase();
        var userId = Guid.NewGuid();
        await using (var db = _database.NewContext())
            await new WalletRepository(db).GetOrCreateForUserAsync(userId, "COP", DateTime.UtcNow);

        await using var first = _database.NewContext();
        await using var second = _database.NewContext();
        var copyA = await first.Wallets.SingleAsync(w => w.OwnerId == userId);
        var copyB = await second.Wallets.SingleAsync(w => w.OwnerId == userId);

        first.WalletEntries.Add(copyA.Apply(WalletEntryType.TopUp, 100m, DateTime.UtcNow));
        await first.SaveChangesAsync();
        second.WalletEntries.Add(copyB.Apply(WalletEntryType.TopUp, 100m, DateTime.UtcNow));
        var action = () => second.SaveChangesAsync();

        await action.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var check = _database.NewContext();
        (await check.Wallets.SingleAsync(w => w.OwnerId == userId)).AvailableBalance.Should().Be(100m);
    }

    [SkippableFact]
    public async Task CommissionRules_ShouldReturnTheOneValidAtEachInstant()
    {
        RequireDatabase();
        var later = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = _database.NewContext())
        {
            db.CommissionRules.Add(new CommissionRule(Guid.NewGuid(), 0.10m, later, null, "tarifa futura", DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        await using var read = _database.NewContext();
        var rules = new CommissionRuleRepository(read);
        (await rules.GetValidAtAsync(new DateTime(2029, 12, 31, 23, 59, 59, DateTimeKind.Utc)))!.Rate.Should().Be(0.15m);
        (await rules.GetValidAtAsync(later))!.Rate.Should().Be(0.10m);
        (await rules.GetValidAtAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Should().BeNull();
    }
}
