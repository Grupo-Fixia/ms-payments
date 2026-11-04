using Fixia.Payments.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.Infrastructure.Persistence.Inbox;

/// <summary>Un evento de ms-services que ya produjo su efecto en pagos (GC-484, GC-485).</summary>
public class ProcessedEvent
{
    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public DateTime ProcessedAt { get; private set; }

    // Constructor para EF Core
    private ProcessedEvent() { }

    public ProcessedEvent(Guid eventId, string eventType, DateTime processedAt)
    {
        EventId = eventId;
        EventType = eventType;
        ProcessedAt = processedAt;
    }
}

/// <summary>
/// <see cref="IProcessedEventStore"/> sobre <c>processed_events</c>. La clave primaria es el <c>eventId</c>: aunque dos
/// reentregas llegaran a la vez, solo una puede confirmar su efecto.
/// </summary>
public sealed class EfProcessedEventStore : IProcessedEventStore
{
    private readonly PaymentsDbContext _context;

    public EfProcessedEventStore(PaymentsDbContext context)
    {
        _context = context;
    }

    public Task<bool> IsProcessedAsync(Guid eventId, CancellationToken ct = default) =>
        _context.ProcessedEvents.AnyAsync(e => e.EventId == eventId, ct);

    public void Add(Guid eventId, string eventType, DateTime processedAt) =>
        _context.ProcessedEvents.Add(new ProcessedEvent(eventId, eventType, processedAt));
}
