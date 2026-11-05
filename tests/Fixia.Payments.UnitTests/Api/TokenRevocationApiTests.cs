using System.Net;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>
/// GC-502: un access token cerrado con logout deja de valer en ms-payments, igual que en ms-users y ms-services. Si
/// ms-users no puede confirmar el estado, la petición se rechaza (falla cerrado).
/// </summary>
public class TokenRevocationApiTests : IDisposable
{
    private const string Wallet = "/api/payments/wallets/me";
    private readonly PaymentsApiFactory _factory = new PaymentsApiFactory().Seeded();

    public void Dispose() => _factory.Dispose();

    private static string JtiOf(string token) => new JsonWebToken(token).Id;

    [Fact]
    public async Task ATokenThatWasNotRevoked_ShouldBeAccepted()
    {
        var token = _factory.Token(Guid.NewGuid(), "ROLE_CLIENT");

        (await _factory.ClientWithToken(token).GetAsync(Wallet)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ARevokedToken_ShouldReturn401()
    {
        var token = _factory.Token(Guid.NewGuid(), "ROLE_CLIENT");
        var client = _factory.ClientWithToken(token);
        (await client.GetAsync(Wallet)).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Revocations.Revoke(JtiOf(token));

        (await client.GetAsync(Wallet)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "el mismo token después del logout ya no sirve");
    }

    [Fact]
    public async Task WhenUsersCannotConfirm_ShouldFailClosedWith401()
    {
        var token = _factory.Token(Guid.NewGuid(), "ROLE_CLIENT");
        _factory.Revocations.Unavailable = true;

        (await _factory.ClientWithToken(token).GetAsync(Wallet)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ATokenWithoutJti_ShouldReturn401()
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = PaymentsApiFactory.Issuer,
            Audience = PaymentsApiFactory.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = $"usr_{Guid.NewGuid()}",
                ["roles"] = new[] { "ROLE_CLIENT" }
            },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_factory.SigningKey),
                SecurityAlgorithms.RsaSha256)
        });

        (await _factory.ClientWithToken(token).GetAsync(Wallet)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InternalRoutes_ShouldNotDependOnTokenRevocation()
    {
        _factory.Revocations.Unavailable = true;

        var response = await _factory.InternalClient().GetAsync($"/internal/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
