using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Bases que quedaron con el esquema de <c>EnsureCreated()</c> de la versión anterior (GC-477). Se omiten sin
/// <c>FIXIA_PAYMENTS_TEST_PG</c>.
/// </summary>
public class LegacySchemaPostgresTests : IClassFixture<PostgresDatabase>
{
    // Lo que creaba EnsureCreated() antes de GC-477: sin historial de migraciones y con índice no único.
    private const string LegacySchema = @"
        CREATE TABLE payments (id uuid PRIMARY KEY, service_request_id uuid NOT NULL, client_id uuid NOT NULL,
            provider_id uuid NOT NULL, amount numeric(18,2) NOT NULL, currency varchar(10) NOT NULL,
            status varchar(30) NOT NULL, method varchar(30) NOT NULL, transaction_reference varchar(100),
            failure_reason varchar(500), created_at timestamptz NOT NULL, updated_at timestamptz);
        CREATE INDEX ix_payments_service_request_id ON payments (service_request_id);
        CREATE TABLE payment_transactions (id uuid PRIMARY KEY, payment_id uuid NOT NULL REFERENCES payments(id),
            transaction_type varchar(50) NOT NULL, amount numeric(18,2) NOT NULL, status varchar(30) NOT NULL,
            gateway_transaction_id varchar(100), gateway_response text, created_at timestamptz NOT NULL);";

    private readonly PostgresDatabase _server;

    public LegacySchemaPostgresTests(PostgresDatabase server)
    {
        _server = server;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task MigrateAsync(string connectionString)
    {
        var services = new ServiceCollection()
            .AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(connectionString))
            .BuildServiceProvider();
        return DatabaseMigrator.ApplyMigrationsAsync(services, new ConfigurationBuilder().Build(),
            NullLogger.Instance);
    }

    [SkippableFact]
    public async Task EmptyLegacySchema_ShouldBeReplacedByTheMigratedOne()
    {
        Skip.IfNot(_server.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}");
        var connectionString = await _server.CreateScratchDatabaseAsync();
        try
        {
            await ExecuteAsync(connectionString, LegacySchema);

            await MigrateAsync(connectionString);

            await using var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
                .UseNpgsql(connectionString).Options);
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.OutboxEvents.CountAsync()).Should().Be(0);
        }
        finally
        {
            await _server.DropScratchAsync(connectionString);
        }
    }

    [SkippableFact]
    public async Task LegacySchemaWithData_ShouldStopWithoutTouchingIt()
    {
        Skip.IfNot(_server.IsAvailable, $"Sin {PostgresDatabase.EnvironmentVariable}");
        var connectionString = await _server.CreateScratchDatabaseAsync();
        try
        {
            await ExecuteAsync(connectionString, LegacySchema + @"
                INSERT INTO payments VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(),
                    1000, 'COP', 'Pending', 'CreditCard', NULL, NULL, now(), NULL);");

            var action = () => MigrateAsync(connectionString);

            await action.Should().ThrowAsync<LegacySchemaWithDataException>();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM payments", connection);
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(1, "los datos no se tocan");
        }
        finally
        {
            await _server.DropScratchAsync(connectionString);
        }
    }
}
