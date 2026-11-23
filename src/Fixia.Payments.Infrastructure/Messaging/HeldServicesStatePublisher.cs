using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fixia.Payments.Infrastructure.Messaging;

/// <summary>Configuración del publicador de estados replicados (sección <c>ReplicatedStates</c>, GC-547).</summary>
public sealed class ReplicatedStatesOptions
{
    public const string SectionName = "ReplicatedStates";

    public bool PublisherEnabled { get; set; } = true;
    public int IntervalMilliseconds { get; set; } = 3000;

    /// <summary>Cada cuánto se vuelve a publicar todo, para que un consumidor nuevo lo encuentre pese a la retención.</summary>
    public int RefreshHours { get; set; } = 6;
}

/// <summary>
/// Publica <c>pago.retencion</c> en <c>fixia.payments.held-services</c> (GC-547, cero dependencias síncronas): por cada
/// solicitud pagada con billetera, su prestador y si el dinero sigue retenido. ms-users guarda la copia y decide con ella
/// si un técnico puede desvincularse, en lugar de llamar a <c>/internal/payments/held</c>.
///
/// Cada pasada compara el estado de cada solicitud con el hash de lo último publicado y escribe en el Outbox solo lo que
/// cambió. Los hashes viven en memoria: al arrancar se vuelve a publicar todo una vez, lo que no hace daño porque el
/// consumidor se queda con la versión de mayor secuencia (el instante de publicación en milisegundos).
/// </summary>
public sealed class HeldServicesStatePublisher : BackgroundService
{
    public const string StateType = "pago.retencion";
    public const string Topic = "fixia.payments.held-services";
    public const string AggregateType = "HeldService";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IServiceScopeFactory _scopes;
    private readonly ReplicatedStatesOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HeldServicesStatePublisher> _logger;
    private readonly Dictionary<Guid, (string Hash, long Sequence, DateTimeOffset PublishedAt)> _published = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public HeldServicesStatePublisher(IServiceScopeFactory scopes, IOptions<ReplicatedStatesOptions> options,
        TimeProvider time, ILogger<HeldServicesStatePublisher> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(_options.IntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("No se pudieron publicar los estados de retención; se reintenta ({Error})",
                    ex.GetType().Name);
            }

            await Task.Delay(interval, _time, stoppingToken);
        }
    }

    /// <returns>Cuántos estados se escribieron en el Outbox.</returns>
    public async Task<int> PublishChangesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopes.CreateScope();
            var payments = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
            var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var now = _time.GetUtcNow();
            var refresh = TimeSpan.FromHours(_options.RefreshHours);
            var pending = new Dictionary<Guid, (string Hash, long Sequence, DateTimeOffset PublishedAt)>();
            var seen = new HashSet<Guid>();

            foreach (var state in await payments.ListHoldStatesAsync(ct))
            {
                seen.Add(state.ServiceRequestId);
                var data = new { serviceRequestId = state.ServiceRequestId, providerId = state.ProviderId, held = state.Held };
                var hash = Sha256(JsonSerializer.Serialize(data, Json));
                _published.TryGetValue(state.ServiceRequestId, out var previous);
                var changed = previous.Hash is null || previous.Hash != hash || previous.PublishedAt + refresh < now;
                if (!changed)
                    continue;
                var sequence = NextSequence(previous.Sequence, now);
                context.OutboxEvents.Add(Event(state.ServiceRequestId, sequence, removed: false, data, now));
                pending[state.ServiceRequestId] = (hash, sequence, now);
            }

            var gone = _published.Keys.Where(key => !seen.Contains(key)).ToList();
            foreach (var key in gone)
            {
                context.OutboxEvents.Add(Event(key, NextSequence(_published[key].Sequence, now), removed: true,
                    new { }, now));
            }

            if (pending.Count == 0 && gone.Count == 0)
                return 0;
            await context.SaveChangesAsync(ct);
            foreach (var (key, value) in pending)
                _published[key] = value;
            foreach (var key in gone)
                _published.Remove(key);
            return pending.Count + gone.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static long NextSequence(long previous, DateTimeOffset now) =>
        Math.Max(now.ToUnixTimeMilliseconds(), previous + 1);

    private static OutboxEvent Event(Guid key, long sequence, bool removed, object data, DateTimeOffset now)
    {
        var eventId = Guid.NewGuid();
        var envelope = new
        {
            eventId,
            eventType = StateType,
            schemaVersion = 1,
            occurredAt = now.UtcDateTime,
            producer = OutboxWriter.Producer,
            aggregate = new { type = AggregateType, id = key, sequence },
            removed,
            data
        };
        return OutboxEvent.Pending(eventId, StateType, 1, AggregateType, key, sequence, Topic, key.ToString(),
            JsonSerializer.Serialize(envelope, Json), now.UtcDateTime);
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
