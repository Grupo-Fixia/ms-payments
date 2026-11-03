namespace Fixia.Payments.Application.Common.Interfaces;

/// <summary>
/// Bandeja de entrada (GC-484, GC-485): los eventos de ms-services que ya produjeron su efecto. El registro se agrega a
/// la misma unidad de trabajo que el efecto, así que un evento repetido (reentrega de Kafka o del relay con el mismo
/// <c>eventId</c>) nunca retiene ni cobra dos veces.
/// </summary>
public interface IProcessedEventStore
{
    Task<bool> IsProcessedAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>Lo marca como procesado; se confirma con el próximo <c>SaveChangesAsync</c>.</summary>
    void Add(Guid eventId, string eventType, DateTime processedAt);
}
