using Fixia.Payments.Application.Common.Events;

namespace Fixia.Payments.Application.Common.Interfaces;

/// <summary>
/// Outbox transaccional (GC-477): agrega el evento a la unidad de trabajo actual, así que solo existe si el cambio
/// de negocio se confirma en el mismo <c>SaveChangesAsync</c>. Nada se publica en Kafka desde aquí: lo hace el relay.
/// </summary>
public interface IBusinessEventOutbox
{
    Task EnqueueAsync(BusinessEvent businessEvent, CancellationToken ct = default);
}
