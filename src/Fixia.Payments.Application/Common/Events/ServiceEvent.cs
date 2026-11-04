using System.Globalization;
using System.Text.Json;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Application.Common.Events;

/// <summary>
/// Hecho de ms-services leído de <c>fixia.services.service-requests</c> (contrato GC-436 de ms-services). Solo se
/// interpretan los campos que usa ms-payments; el resto del sobre se ignora (lector tolerante).
/// </summary>
public sealed record ServiceEvent(
    Guid EventId,
    string EventType,
    short SchemaVersion,
    DateTime OccurredAt,
    Guid? ActorUserId,
    string ActorRole,
    JsonElement Data)
{
    public const string Topic = "fixia.services.service-requests";
    public const string Contracted = "servicio.contratado";
    public const string Closed = "servicio.cerrado";
    public const string ClosureObjected = "servicio.cierre_objetado";

    /// <summary>Lee el sobre JSON tal como llega de Kafka.</summary>
    /// <exception cref="InvalidServiceEventException">Si el mensaje no tiene la forma del contrato.</exception>
    public static ServiceEvent Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var actor = root.GetProperty("actor");
            var userId = actor.GetProperty("userId");
            return new ServiceEvent(
                root.GetProperty("eventId").GetGuid(),
                root.GetProperty("eventType").GetString() ?? throw new InvalidServiceEventException("eventType vacío"),
                root.GetProperty("schemaVersion").GetInt16(),
                root.GetProperty("occurredAt").GetDateTime().ToUniversalTime(),
                userId.ValueKind == JsonValueKind.Null ? null : userId.GetGuid(),
                actor.GetProperty("role").GetString() ?? "SYSTEM",
                root.GetProperty("data").Clone());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidServiceEventException($"El mensaje no cumple el sobre de eventos: {ex.Message}");
        }
    }

    public Guid DataGuid(string property)
    {
        try
        {
            return Data.GetProperty(property).GetGuid();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidServiceEventException($"{EventType}: falta o no es válido data.{property}");
        }
    }

    /// <summary>Lee un importe <c>{"amount": "105000.00", "currency": "COP"}</c>.</summary>
    public (decimal Amount, string Currency) DataMoney(string property)
    {
        try
        {
            var money = Data.GetProperty(property);
            var amount = decimal.Parse(money.GetProperty("amount").GetString()!, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture);
            return (amount, money.GetProperty("currency").GetString()!);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException
                                       or ArgumentNullException)
        {
            throw new InvalidServiceEventException($"{EventType}: falta o no es válido data.{property}");
        }
    }

    public EventActor Actor => new(ActorUserId, ActorRole);
}

/// <summary>Mensaje que no cumple el contrato: no se puede procesar y reintentarlo no sirve.</summary>
public class InvalidServiceEventException : PaymentDomainException
{
    public InvalidServiceEventException(string message) : base(message)
    {
    }
}
