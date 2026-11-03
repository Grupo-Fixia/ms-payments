using System.Globalization;
using System.Text.Json;
using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.Infrastructure.Persistence.Outbox;

/// <summary>
/// Escribe el evento en <c>outbox_events</c> dentro de la unidad de trabajo actual (GC-477). No guarda: el evento se
/// confirma con el mismo <c>SaveChangesAsync</c> que el cambio de negocio, o no existe.
/// <para>
/// El sobre es el de ms-services (<c>envelope.v1</c>) con <c>producer = ms-payments</c>: el agregado es el pago
/// (<c>aggregate.type = Payment</c>, secuencia propia 1, 2, 3…) y <b>la clave del mensaje es <c>aggregate.id</c></b> (el
/// pago), como exige el contrato de GC-436 y valida el lakehouse (GC-435). La correlación con el servicio va en
/// <c>correlationId</c> (la solicitud). Como hay un solo pago por solicitud, ordenar por pago es ordenar por servicio.
/// </para>
/// </summary>
public sealed class OutboxWriter : IBusinessEventOutbox
{
    public const string Producer = "ms-payments";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly PaymentsDbContext _context;

    public OutboxWriter(PaymentsDbContext context)
    {
        _context = context;
    }

    public async Task EnqueueAsync(BusinessEvent businessEvent, CancellationToken ct = default)
    {
        var sequence = await NextSequenceAsync(businessEvent.PaymentId, ct);
        var eventId = Guid.NewGuid();
        var occurredAt = ToMicroseconds(businessEvent.OccurredAt);

        var envelope = new
        {
            eventId,
            eventType = businessEvent.EventType,
            schemaVersion = businessEvent.SchemaVersion,
            occurredAt = occurredAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            producer = Producer,
            aggregate = new
            {
                type = OutboxEvent.AggregateTypePayment,
                id = businessEvent.PaymentId,
                sequence
            },
            correlationId = businessEvent.ServiceRequestId,
            actor = new
            {
                userId = businessEvent.Actor.UserId,
                role = businessEvent.Actor.Role
            },
            data = businessEvent.Data
        };

        var outboxEvent = OutboxEvent.Pending(
            eventId,
            businessEvent.EventType,
            businessEvent.SchemaVersion,
            OutboxEvent.AggregateTypePayment,
            businessEvent.PaymentId,
            sequence,
            PaymentEvents.Topic,
            businessEvent.PaymentId.ToString(),
            JsonSerializer.Serialize(envelope, JsonOptions),
            occurredAt);

        await _context.OutboxEvents.AddAsync(outboxEvent, ct);
    }

    /// <summary>
    /// Siguiente número del pago: el mayor ya guardado o pendiente en esta unidad de trabajo, más uno. Dos
    /// transacciones simultáneas sobre el mismo pago no pueden confirmarse las dos (bloqueo optimista del pago), y la
    /// restricción única <c>(aggregate_id, aggregate_sequence)</c> lo garantiza aunque algo falle.
    /// </summary>
    private async Task<long> NextSequenceAsync(Guid paymentId, CancellationToken ct)
    {
        var stored = await _context.OutboxEvents
            .Where(e => e.AggregateId == paymentId)
            .MaxAsync(e => (long?)e.AggregateSequence, ct) ?? 0;

        var pending = _context.OutboxEvents.Local
            .Where(e => e.AggregateId == paymentId)
            .Select(e => e.AggregateSequence)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(stored, pending) + 1;
    }

    /// <summary>PostgreSQL guarda microsegundos: el instante del sobre y el de la columna deben ser idénticos.</summary>
    private static DateTime ToMicroseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
