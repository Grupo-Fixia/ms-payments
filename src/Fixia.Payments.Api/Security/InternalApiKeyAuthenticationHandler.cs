using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Fixia.Payments.Api.Security;

public sealed class InternalApiKeyOptions : AuthenticationSchemeOptions
{
    /// <summary>La <c>INTERNAL_API_KEY</c> compartida entre microservicios. Vacía: no se acepta ninguna petición.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Autentica las rutas internas (<c>/internal/**</c>) con la clave compartida entre microservicios, igual que el
/// <c>InternalApiKeyFilter</c> de ms-users y ms-services (GC-477): cabecera <c>X-Internal-Api-Key</c>, comparación en
/// tiempo constante sobre el hash. Esas rutas no pasan por Traefik, que solo enruta <c>/api/payments</c>.
/// </summary>
public sealed class InternalApiKeyAuthenticationHandler : AuthenticationHandler<InternalApiKeyOptions>
{
    public const string SchemeName = "InternalApiKey";
    public const string HeaderName = "X-Internal-Api-Key";
    public const string Role = "ROLE_INTERNAL";

    public InternalApiKeyAuthenticationHandler(IOptionsMonitor<InternalApiKeyOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = Options.ApiKey;
        if (string.IsNullOrWhiteSpace(expected))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!Request.Headers.TryGetValue(HeaderName, out var provided) || string.IsNullOrEmpty(provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!CryptographicOperations.FixedTimeEquals(Hash(expected), Hash(provided.ToString())))
            return Task.FromResult(AuthenticateResult.Fail("Clave interna no válida"));

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "internal-service"),
            new Claim(ClaimTypes.Role, Role)
        }, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ProblemResponses.WriteUnauthorizedAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ProblemResponses.WriteForbiddenAsync(Context);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
