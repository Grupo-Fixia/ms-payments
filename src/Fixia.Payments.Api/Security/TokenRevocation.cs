using System.Net.Http.Json;

namespace Fixia.Payments.Api.Security;

/// <summary>
/// Si un token ya fue revocado en ms-users (logout). Cada petición autenticada se comprueba, igual que en ms-services
/// (GC-502): un token cerrado deja de servir en todos los servicios, no solo en ms-users.
/// </summary>
public interface ITokenRevocationChecker
{
    /// <returns><c>true</c> si ms-users informa el token como revocado.</returns>
    /// <exception cref="TokenRevocationCheckException">Si no se pudo obtener una respuesta válida de ms-users.</exception>
    Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default);
}

public sealed class TokenRevocationCheckException : Exception
{
    public TokenRevocationCheckException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Consulta <c>GET /internal/tokens/{jti}/status</c> de ms-users con <c>X-Internal-Api-Key</c>. Un <c>jti</c>
/// desconocido responde <c>revoked=false</c>; cualquier otra respuesta o un fallo de red se trata como "no se pudo
/// verificar" y la petición se rechaza (falla cerrado).
/// </summary>
public sealed class UsersTokenRevocationChecker : ITokenRevocationChecker
{
    public const string DefaultBaseUrl = "http://ms-users:8080";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;

    public UsersTokenRevocationChecker(HttpClient http)
    {
        _http = http;
    }

    private sealed record TokenStatus(bool? Revoked);

    public async Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default)
    {
        TokenStatus? status;
        try
        {
            using var response = await _http.GetAsync($"/internal/tokens/{Uri.EscapeDataString(jti)}/status", ct);
            if (!response.IsSuccessStatusCode)
                throw new TokenRevocationCheckException(
                    $"ms-users respondió {(int)response.StatusCode} al consultar el estado del token");
            status = await response.Content.ReadFromJsonAsync<TokenStatus>(cancellationToken: ct);
        }
        catch (TokenRevocationCheckException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       or NotSupportedException)
        {
            throw new TokenRevocationCheckException("ms-users no pudo confirmar el estado del token", ex);
        }

        if (status?.Revoked is null)
            throw new TokenRevocationCheckException("ms-users respondió sin el estado del token");
        return status.Revoked.Value;
    }
}
