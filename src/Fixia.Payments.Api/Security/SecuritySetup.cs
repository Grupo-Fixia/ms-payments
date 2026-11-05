using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Fixia.Payments.Api.Security;

/// <summary>
/// Seguridad de ms-payments (GC-477), con las mismas reglas que ms-services:
/// <list type="bullet">
/// <item>Las rutas <c>/api/payments/**</c> exigen un token de ms-users: firma RS256 con <c>JWT_PUBLIC_KEY</c>, emisor
/// <c>JWT_ISSUER</c>, audiencia <c>JWT_AUDIENCE</c> y expiración (tolerancia de 5 s). Los roles vienen en el claim
/// <c>roles</c> con prefijo (<c>ROLE_CLIENT</c>) y el usuario en <c>sub</c> (<c>usr_&lt;uuid&gt;</c>).</item>
/// <item>Las rutas <c>/internal/**</c> exigen <c>X-Internal-Api-Key</c> (<c>INTERNAL_API_KEY</c>).</item>
/// <item>Salud y Swagger son públicos.</item>
/// </list>
/// </summary>
public static class SecuritySetup
{
    public const string InternalServicePolicy = "InternalService";
    public const string DefaultIssuer = "https://auth.fixia.com";
    public const string DefaultAudience = "https://api.fixia.com";
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);
    public const string JtiClaim = "jti";

    public static IServiceCollection AddFixiaSecurity(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var issuer = Value(configuration, "JWT_ISSUER") ?? DefaultIssuer;
        var audience = Value(configuration, "JWT_AUDIENCE") ?? DefaultAudience;
        var signingKey = new RsaSecurityKey(LoadPublicKey(configuration, environment));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
                    ClockSkew = ClockSkew,
                    NameClaimType = RequesterExtensions.SubjectClaim,
                    RoleClaimType = RequesterExtensions.RolesClaim
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var subject = context.Principal?.FindFirst(RequesterExtensions.SubjectClaim)?.Value;
                        if (!RequesterExtensions.TryParseSubject(subject, out _))
                        {
                            context.Fail("El sub del token no tiene la forma usr_<uuid>");
                            return;
                        }

                        // GC-502: un token cerrado con logout no vale, igual que en ms-users y ms-services. Sin jti no
                        // se puede comprobar (los tokens de ms-users siempre lo traen). Si ms-users no responde, se
                        // rechaza: falla cerrado.
                        var jti = context.Principal?.FindFirst(JtiClaim)?.Value;
                        if (string.IsNullOrWhiteSpace(jti))
                        {
                            context.Fail("Token revocado");
                            return;
                        }

                        var checker = context.HttpContext.RequestServices.GetRequiredService<ITokenRevocationChecker>();
                        try
                        {
                            if (await checker.IsRevokedAsync(jti, context.HttpContext.RequestAborted))
                                context.Fail("Token revocado");
                        }
                        catch (TokenRevocationCheckException ex)
                        {
                            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                                .CreateLogger(typeof(SecuritySetup))
                                .LogWarning("No se pudo comprobar la revocación de un token; se rechaza la petición ({Error})",
                                    ex.GetType().Name);
                            context.Fail("No se pudo verificar la revocación del token");
                        }
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ProblemResponses.WriteUnauthorizedAsync(context.HttpContext);
                    },
                    OnForbidden = context => ProblemResponses.WriteForbiddenAsync(context.HttpContext)
                };
            })
            .AddScheme<InternalApiKeyOptions, InternalApiKeyAuthenticationHandler>(
                InternalApiKeyAuthenticationHandler.SchemeName,
                options => options.ApiKey = Value(configuration, "INTERNAL_API_KEY"));

        // Revocación de tokens en ms-users (GC-502).
        var usersBaseUrl = Value(configuration, "USERS_BASE_URL") ?? UsersTokenRevocationChecker.DefaultBaseUrl;
        var internalApiKey = Value(configuration, "INTERNAL_API_KEY") ?? string.Empty;
        services.AddHttpClient<ITokenRevocationChecker, UsersTokenRevocationChecker>(client =>
        {
            client.BaseAddress = new Uri(usersBaseUrl);
            client.Timeout = UsersTokenRevocationChecker.Timeout;
            client.DefaultRequestHeaders.Add(InternalApiKeyAuthenticationHandler.HeaderName, internalApiKey);
        });

        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build();
            options.AddPolicy(InternalServicePolicy, policy => policy
                .AddAuthenticationSchemes(InternalApiKeyAuthenticationHandler.SchemeName)
                .RequireRole(InternalApiKeyAuthenticationHandler.Role));
        });

        return services;
    }

    private static System.Security.Cryptography.RSA LoadPublicKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var pem = Value(configuration, "JWT_PUBLIC_KEY");
        if (pem is not null)
            return JwtKeyLoader.FromPem(pem);

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            Console.WriteLine("ADVERTENCIA: sin JWT_PUBLIC_KEY se usa una clave efímera (solo Development y pruebas); "
                              + "los tokens reales de ms-users no se podrán verificar y las rutas protegidas responderán 401.");
            return JwtKeyLoader.Generate();
        }

        throw new InvalidOperationException("JWT_PUBLIC_KEY es obligatoria fuera de Development y pruebas");
    }

    private static string? Value(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
