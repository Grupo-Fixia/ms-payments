using System.Net.Sockets;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fixia.Payments.Infrastructure.Messaging;

/// <summary>Qué hacer con un mensaje cuyo efecto no se pudo guardar.</summary>
public enum ConsumerFailureAction
{
    /// <summary>Volver al mismo mensaje y reintentarlo.</summary>
    Retry,

    /// <summary>Apartarlo (DLT y log de error), confirmar el offset y seguir con la partición.</summary>
    Park
}

/// <summary>
/// Decide si un mensaje que falló se reintenta o se aparta. Un error transitorio (base o Kafka caídos, conflicto de
/// concurrencia, tiempo agotado) se reintenta siempre: no es culpa del mensaje y saltarlo perdería un cobro. Un error
/// que no es transitorio (una regla de negocio, un dato que falta) se repite igual en cada intento; tras
/// <see cref="MaxAttempts"/> intentos el mensaje se aparta para que no bloquee para siempre a los que vienen detrás en
/// su partición (incidente del 2026-11-24: un <c>servicio.cerrado</c> que fallaba siempre dejó a pagos sin procesar
/// ningún evento más).
/// </summary>
public sealed class ConsumerFailurePolicy
{
    private readonly Dictionary<(string Topic, int Partition, long Offset), int> _attempts = new();

    public ConsumerFailurePolicy(int maxAttempts)
    {
        MaxAttempts = Math.Max(1, maxAttempts);
    }

    public int MaxAttempts { get; }

    /// <summary>Registra un fallo del mensaje y devuelve qué hacer y cuántos intentos fallidos lleva.</summary>
    public (ConsumerFailureAction Action, int Attempts) OnFailure(TopicPartitionOffset message, Exception error)
    {
        var key = Key(message);
        if (IsTransient(error))
        {
            _attempts.Remove(key);
            return (ConsumerFailureAction.Retry, 0);
        }

        var attempts = _attempts.GetValueOrDefault(key) + 1;
        if (attempts < MaxAttempts)
        {
            _attempts[key] = attempts;
            return (ConsumerFailureAction.Retry, attempts);
        }

        _attempts.Remove(key);
        return (ConsumerFailureAction.Park, attempts);
    }

    /// <summary>El mensaje se aplicó o se apartó: se olvida su cuenta de intentos.</summary>
    public void OnDone(TopicPartitionOffset message) => _attempts.Remove(Key(message));

    /// <summary>Busca en toda la cadena de excepciones una causa que se arregla sola al reintentar.</summary>
    public static bool IsTransient(Exception error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case DbUpdateConcurrencyException:
                case TimeoutException:
                case SocketException:
                case KafkaException:
                case NpgsqlException { IsTransient: true }:
                    return true;
            }
        }

        return false;
    }

    private static (string, int, long) Key(TopicPartitionOffset message) =>
        (message.Topic, message.Partition.Value, message.Offset.Value);
}
