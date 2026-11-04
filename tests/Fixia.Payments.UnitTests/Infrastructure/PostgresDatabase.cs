using Fixia.Payments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Base PostgreSQL temporal para las pruebas que necesitan el motor real (GC-477): migraciones sobre base vacía,
/// bloqueo optimista con <c>xmin</c>, índices únicos y el relay con <c>FOR UPDATE SKIP LOCKED</c>.
/// <para>
/// Solo corren si existe la variable <c>FIXIA_PAYMENTS_TEST_PG</c> con la cadena de conexión a un servidor (por
/// ejemplo el <c>fixia-db-payments</c> del compose:
/// <c>Host=localhost;Port=5433;Username=user;Password=password;Database=postgres</c>). Crean una base
/// <c>payments_test_*</c> nueva, le aplican las migraciones y la borran al terminar. Sin la variable se omiten.
/// </para>
/// </summary>
public sealed class PostgresDatabase : IAsyncLifetime
{
    public const string EnvironmentVariable = "FIXIA_PAYMENTS_TEST_PG";

    private readonly string? _serverConnection = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private readonly string _databaseName = $"payments_test_{Guid.NewGuid():N}";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_serverConnection);

    public string ConnectionString => new NpgsqlConnectionStringBuilder(_serverConnection)
    {
        Database = _databaseName
    }.ConnectionString;

    public PaymentsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Otra base vacía en el mismo servidor, sin migraciones; se borra con <see cref="DropScratchAsync"/>.</summary>
    public async Task<string> CreateScratchDatabaseAsync()
    {
        var name = $"payments_scratch_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(_serverConnection);
        await server.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", server);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_serverConnection) { Database = name }.ConnectionString;
    }

    public async Task DropScratchAsync(string connectionString)
    {
        NpgsqlConnection.ClearAllPools();
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var server = new NpgsqlConnection(_serverConnection);
        await server.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", server);
        await drop.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
            return;

        await using var server = new NpgsqlConnection(_serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", server))
            await create.ExecuteNonQueryAsync();

        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable)
            return;

        NpgsqlConnection.ClearAllPools();
        await using var server = new NpgsqlConnection(_serverConnection);
        await server.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", server);
        await drop.ExecuteNonQueryAsync();
    }
}
