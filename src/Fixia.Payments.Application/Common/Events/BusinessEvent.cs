namespace Fixia.Payments.Application.Common.Events;

/// <summary>
/// Quién causó un hecho: <c>UserId</c> es el <c>users.id</c> (null si fue el sistema u otro microservicio) y
/// <c>Role</c> es <c>CLIENT</c>, <c>PROFESSIONAL</c>, <c>COMPANY</c>, <c>ADMIN</c> o <c>SYSTEM</c>.
/// </summary>
public sealed record EventActor(Guid? UserId, string Role)
{
    public static readonly EventActor System = new(null, "SYSTEM");
}

/// <summary>
/// Hecho de negocio de ms-payments, listo para el Outbox (GC-477). El sobre lo arma la infraestructura con el mismo
/// formato que ms-services: el agregado es el pago y la correlación es la solicitud de servicio.
/// </summary>
public sealed record BusinessEvent(
    string EventType,
    short SchemaVersion,
    Guid PaymentId,
    Guid ServiceRequestId,
    DateTime OccurredAt,
    EventActor Actor,
    object Data);
