using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fixia.Payments.Infrastructure.Messaging;

/// <summary>
/// Publica en Kafka lo pendiente del Outbox (GC-477), con las reglas del relay de ms-services:
/// <list type="number">
/// <item>Toma eventos <c>PENDING</c> con <c>next_attempt_at</c> vencido, en orden de <c>occurred_at</c> y secuencia,
/// con <c>FOR UPDATE SKIP LOCKED</c> para que dos réplicas no publiquen el mismo.</item>
/// <item>No publica la secuencia n + 1 de un pago mientras la n no esté publicada.</item>
/// <item>Marca <c>PUBLISHED</c> solo después del acuse del broker. Si el proceso cae entre el acuse y la marca, el
/// evento se vuelve a publicar con el mismo <c>eventId</c>: es una reentrega permitida (no hay exactly-once).</item>
/// <item>Si falla, suma un intento con espera exponencial; tras 10 intentos queda <c>FAILED</c>.</item>
/// </list>
/// Si Kafka está caído los eventos esperan en la base y salen cuando vuelve: no se pierde ninguno.
/// </summary>
public sealed class OutboxRelay : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagePublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxRelay> _logger;

    public OutboxRelay(
        IServiceScopeFactory scopeFactory,
        IMessagePublisher publisher,
        TimeProvider timeProvider,
        IOptions<OutboxOptions> options,
        ILogger<OutboxRelay> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Relay del Outbox iniciado (ronda cada {Interval} ms, lote de {Batch}).",
            _options.PollIntervalMilliseconds, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                published = await PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Base caída o error inesperado: se reintenta en la próxima ronda sin tumbar el servicio.
                _logger.LogWarning(ex, "La ronda del relay del Outbox falló; se reintentará.");
            }

            if (published == 0)
            {
                try
                {
                    await Task.Delay(_options.PollIntervalMilliseconds, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Una ronda: toma un lote, lo publica y guarda el resultado. Devuelve cuántos quedaron publicados.</summary>
    public async Task<int> PublishPendingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var pending = OutboxStatus.Pending.ToString().ToUpperInvariant();
        var batchSize = _options.BatchSize;

        var batch = await db.OutboxEvents
            .FromSqlInterpolated($@"
                SELECT o.* FROM outbox_events o
                WHERE o.status = {pending}
                  AND o.next_attempt_at <= {now}
                  AND NOT EXISTS (
                      SELECT 1 FROM outbox_events p
                      WHERE p.aggregate_id = o.aggregate_id
                        AND p.aggregate_sequence < o.aggregate_sequence
                        AND p.status <> 'PUBLISHED')
                ORDER BY o.occurred_at, o.aggregate_sequence
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);

        if (batch.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        var published = 0;
        foreach (var outboxEvent in batch)
        {
            try
            {
                await _publisher.PublishAsync(ToMessage(outboxEvent), ct);
                outboxEvent.MarkPublished(_timeProvider.GetUtcNow().UtcDateTime);
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // El error no incluye el contenido del evento.
                outboxEvent.RegisterFailedAttempt($"{ex.GetType().Name}: {ex.Message}", _timeProvider.GetUtcNow().UtcDateTime);
                _logger.LogWarning("No se pudo publicar el evento {EventId} ({EventType}, intento {Attempt}): {Error}",
                    outboxEvent.EventId, outboxEvent.EventType, outboxEvent.Attempts, ex.Message);
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return published;
    }

    /// <summary>Clave y cabeceras de la sección 3 del contrato: las cabeceras repiten datos del cuerpo.</summary>
    public static BrokerMessage ToMessage(OutboxEvent outboxEvent) =>
        new(outboxEvent.Topic, outboxEvent.MessageKey, outboxEvent.Payload, new Dictionary<string, string>
        {
            ["eventId"] = outboxEvent.EventId.ToString(),
            ["eventType"] = outboxEvent.EventType,
            ["schemaVersion"] = outboxEvent.SchemaVersion.ToString(),
            ["content-type"] = "application/json"
        });
}
