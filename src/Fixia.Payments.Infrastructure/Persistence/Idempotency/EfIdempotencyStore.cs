using System.Text.Json;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fixia.Payments.Infrastructure.Persistence.Idempotency;

/// <summary>
/// <see cref="IIdempotencyStore"/> sobre <c>idempotency_keys</c> (GC-482, GC-483).
/// <para>
/// La fila de la clave se inserta al empezar, dentro de la transacción de la operación. Si llega otra petición con la
/// misma clave al mismo tiempo, PostgreSQL la hace esperar en el índice único hasta que la primera confirme, y entonces
/// falla con violación de unicidad: en ese caso se lee la respuesta guardada y se devuelve, sin ejecutar la acción otra
/// vez. Si la primera falla, su fila desaparece con el rollback y la clave queda libre para reintentar.
/// </para>
/// </summary>
public sealed class EfIdempotencyStore : IIdempotencyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentsDbContext _context;
    private readonly TimeProvider _clock;

    public EfIdempotencyStore(PaymentsDbContext context, TimeProvider clock)
    {
        _context = context;
        _clock = clock;
    }

    public async Task<IdempotentResult<T>> RunAsync<T>(Guid userId, string operation, string key, string requestHash,
        Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        // Camino normal de un reintento: la clave ya está confirmada.
        var existing = await FindAsync(userId, operation, key, ct);
        if (existing != null)
            return Replay<T>(existing, requestHash);

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        var record = IdempotencyRecord.Start(userId, operation, key, requestHash, Now());
        _context.IdempotencyRecords.Add(record);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation
                                           })
        {
            // Otra petición con la misma clave confirmó mientras esta esperaba.
            await transaction.RollbackAsync(ct);
            _context.Entry(record).State = EntityState.Detached;
            var winner = await FindAsync(userId, operation, key, ct)
                         ?? throw new PaymentConflictException("La operación con esa Idempotency-Key no se pudo leer; reintente.");
            return Replay<T>(winner, requestHash);
        }

        var result = await action(ct);

        record.Complete(JsonSerializer.Serialize(result, JsonOptions), Now());
        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new IdempotentResult<T>(result, Replayed: false);
    }

    private Task<IdempotencyRecord?> FindAsync(Guid userId, string operation, string key, CancellationToken ct) =>
        _context.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.Operation == operation && r.Key == key, ct);

    private static IdempotentResult<T> Replay<T>(IdempotencyRecord record, string requestHash)
    {
        if (!string.Equals(record.RequestHash, requestHash, StringComparison.Ordinal))
            throw new IdempotencyKeyReuseException();
        if (record.ResponseJson is null)
            throw new PaymentConflictException("La operación con esa Idempotency-Key todavía está en curso; reintente.");

        return new IdempotentResult<T>(JsonSerializer.Deserialize<T>(record.ResponseJson, JsonOptions)!, Replayed: true);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
