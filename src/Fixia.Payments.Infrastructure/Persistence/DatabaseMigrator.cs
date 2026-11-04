using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Infrastructure.Persistence;

/// <summary>
/// Aplica las migraciones versionadas al arrancar (GC-477). Reemplaza a <c>EnsureCreated()</c>, que creaba el esquema
/// una sola vez y nunca lo actualizaba.
/// <para>
/// Se desactiva con <c>Database__ApplyMigrationsOnStartup=false</c> cuando el ambiente aplica las migraciones aparte
/// (por ejemplo, con un <i>migration bundle</i> en un Job antes del despliegue en QA). Si la base no responde, reintenta
/// unas veces y luego falla: es mejor que el servicio no arranque a que arranque sin su esquema.
/// </para>
/// </summary>
public static class DatabaseMigrator
{
    public const string ApplyOnStartupKey = "Database:ApplyMigrationsOnStartup";
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    public static async Task ApplyMigrationsAsync(IServiceProvider services, IConfiguration configuration,
        ILogger logger, CancellationToken ct = default)
    {
        if (!configuration.GetValue(ApplyOnStartupKey, true))
        {
            logger.LogInformation("Migraciones al arrancar desactivadas ({Key}=false).", ApplyOnStartupKey);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
                if (!db.Database.IsRelational())
                    return; // Proveedor en memoria de las pruebas: no tiene esquema que migrar.

                await ReplaceEmptyLegacySchemaAsync(db, logger, ct);

                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count > 0)
                    logger.LogInformation("Aplicando {Count} migración(es) de ms-payments: {Migrations}",
                        pending.Count, string.Join(", ", pending));

                await db.Database.MigrateAsync(ct);
                logger.LogInformation("Esquema de base de datos de ms-payments al día.");
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is not OperationCanceledException
                                                              and not LegacySchemaWithDataException)
            {
                logger.LogWarning("No se pudieron aplicar las migraciones (intento {Attempt} de {Max}): {Error}",
                    attempt, MaxAttempts, ex.Message);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>
    /// La versión anterior creaba <c>payments</c> y <c>payment_transactions</c> con <c>EnsureCreated()</c>, sin
    /// historial de migraciones. Si una base quedó así, la primera migración fallaría al intentar crear tablas que ya
    /// existen. Si esas tablas están vacías se borran y la migración las crea de nuevo con el esquema correcto; si
    /// tienen datos no se toca nada y el servicio no arranca, para que alguien decida qué hacer con ellos.
    /// </summary>
    private static async Task ReplaceEmptyLegacySchemaAsync(PaymentsDbContext db, ILogger logger, CancellationToken ct)
    {
        var hasHistory = await ScalarAsync<bool>(db, "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL", ct);
        var hasLegacyPayments = await ScalarAsync<bool>(db, "SELECT to_regclass('public.payments') IS NOT NULL", ct);
        if (hasHistory || !hasLegacyPayments)
            return;

        var hasData = await ScalarAsync<bool>(db,
            "SELECT EXISTS (SELECT 1 FROM payments) OR "
            + "(to_regclass('public.payment_transactions') IS NOT NULL AND EXISTS (SELECT 1 FROM payment_transactions))", ct);
        if (hasData)
            throw new LegacySchemaWithDataException();

        logger.LogWarning("Se encontró el esquema vacío creado por EnsureCreated(); se reemplaza por el de las migraciones.");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS payment_transactions, payments", ct);
    }

    private static async Task<T> ScalarAsync<T>(PaymentsDbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)(await command.ExecuteScalarAsync(ct))!;
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }
}

/// <summary>
/// La base tiene tablas de pagos con datos pero sin historial de migraciones: no se migra automáticamente.
/// </summary>
public sealed class LegacySchemaWithDataException : InvalidOperationException
{
    public LegacySchemaWithDataException()
        : base("payments_db tiene tablas creadas por EnsureCreated() con datos y sin historial de migraciones. "
               + "Respalde los datos, borre las tablas payments y payment_transactions (o la base) y reinicie el servicio "
               + "para que las migraciones creen el esquema.")
    {
    }
}
