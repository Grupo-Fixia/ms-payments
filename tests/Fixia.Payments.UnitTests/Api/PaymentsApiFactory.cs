using System.Net.Http.Headers;
using System.Security.Cryptography;
using Fixia.Payments.Api.Security;
using Fixia.Payments.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>
/// La API real con base en memoria y sin relay (GC-477). Firma sus propios tokens con una clave RSA de prueba, cuya
/// parte pública se entrega como <c>JWT_PUBLIC_KEY</c>, igual que en el ambiente.
/// </summary>
public sealed class PaymentsApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://auth.fixia.com";
    public const string Audience = "https://api.fixia.com";
    public const string InternalApiKey = "clave-interna-de-prueba";

    private readonly RSA _signingKey = RSA.Create(2048);
    private readonly string _databaseName = $"payments-api-{Guid.NewGuid()}";

    /// <summary>ms-users simulado para la revocación de tokens (GC-502): por defecto ningún token está revocado.</summary>
    public FakeTokenRevocationChecker Revocations { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JWT_PUBLIC_KEY", _signingKey.ExportSubjectPublicKeyInfoPem());
        builder.UseSetting("JWT_ISSUER", Issuer);
        builder.UseSetting("JWT_AUDIENCE", Audience);
        builder.UseSetting("INTERNAL_API_KEY", InternalApiKey);
        builder.UseSetting("Outbox:RelayEnabled", "false");
        builder.UseSetting("ServiceEvents:ConsumerEnabled", "false");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITokenRevocationChecker>();
            services.AddSingleton<ITokenRevocationChecker>(Revocations);

            var registration = services.Single(d => d.ServiceType == typeof(DbContextOptions<PaymentsDbContext>));
            services.Remove(registration);
            // La base en memoria no tiene transacciones: se ignoran (las pruebas con PostgreSQL cubren esa parte).
            services.AddDbContext<PaymentsDbContext>(options => options
                .UseInMemoryDatabase(_databaseName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        });
    }

    public string Token(Guid userId, params string[] roles) =>
        CreateToken($"usr_{userId}", roles, Issuer, Audience, DateTime.UtcNow.AddMinutes(15), _signingKey);

    public static string CreateToken(string subject, string[] roles, string issuer, string audience, DateTime expires,
        RSA key)
    {
        var issuedAt = expires.AddMinutes(-15);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["roles"] = roles,
                ["jti"] = Guid.NewGuid().ToString()
            },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)
        });
    }

    public RSA SigningKey => _signingKey;

    /// <summary>
    /// La base en memoria no aplica migraciones: <c>EnsureCreated</c> carga los datos sembrados por el modelo (la cuenta
    /// de Fixia y la tarifa inicial de 15 %).
    /// </summary>
    public PaymentsApiFactory Seeded()
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.EnsureCreated();
        return this;
    }

    public HttpClient ClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient InternalClient(string apiKey = InternalApiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", apiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _signingKey.Dispose();
    }
}

/// <summary>Revocación de tokens controlada por la prueba: revocados por jti, o ms-users caído.</summary>
public sealed class FakeTokenRevocationChecker : ITokenRevocationChecker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _revoked = new();

    public bool Unavailable { get; set; }

    public void Revoke(string jti) => _revoked[jti] = true;

    public Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default)
    {
        if (Unavailable)
            throw new TokenRevocationCheckException("ms-users simulado no responde");
        return Task.FromResult(_revoked.ContainsKey(jti));
    }
}
