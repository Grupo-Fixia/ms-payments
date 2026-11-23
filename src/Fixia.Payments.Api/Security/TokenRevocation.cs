namespace Fixia.Payments.Api.Security;

/// <summary>
/// Si un token ya fue revocado en ms-users (logout). Cada petición autenticada se comprueba, igual que en ms-services
/// (GC-502): un token cerrado deja de servir en todos los servicios. Desde GC-547 se responde con la lista local
/// (<see cref="ReplicatedTokenRevocationChecker"/>), sin llamar a ms-users.
/// </summary>
public interface ITokenRevocationChecker
{
    /// <returns><c>true</c> si el token fue revocado.</returns>
    /// <exception cref="TokenRevocationCheckException">Si no se puede saber (la lista local no está al día).</exception>
    Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default);
}

public sealed class TokenRevocationCheckException : Exception
{
    public TokenRevocationCheckException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
