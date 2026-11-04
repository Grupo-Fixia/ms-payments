namespace Fixia.Payments.Application.Common.Interfaces;

/// <summary>Resultado de una operación idempotente: <c>Replayed</c> es true si se devolvió la respuesta guardada.</summary>
public sealed record IdempotentResult<T>(T Value, bool Replayed);

/// <summary>
/// Operaciones con <c>Idempotency-Key</c> (GC-482, GC-483). La clave es por usuario y por operación.
/// <list type="bullet">
/// <item>Primera vez: ejecuta la acción y guarda su respuesta en la <b>misma transacción</b> que sus cambios.</item>
/// <item>Misma clave y mismo cuerpo: devuelve la respuesta guardada sin volver a ejecutar nada.</item>
/// <item>Misma clave con otro cuerpo: <c>IdempotencyKeyReuseException</c> (422).</item>
/// <item>Si la acción falla no queda nada guardado: se puede reintentar con la misma clave.</item>
/// </list>
/// Dos peticiones simultáneas con la misma clave no ejecutan la acción dos veces.
/// </summary>
public interface IIdempotencyStore
{
    Task<IdempotentResult<T>> RunAsync<T>(Guid userId, string operation, string key, string requestHash,
        Func<CancellationToken, Task<T>> action, CancellationToken ct = default);
}
