using System.Net;
using Fixia.Payments.Api.Middlewares;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>GC-512: las respuestas con saldos y pagos no se guardan en cachés, ni siquiera las de error.</summary>
public class NoStoreCacheApiTests : IClassFixture<PaymentsApiFactory>
{
    private readonly PaymentsApiFactory _factory;

    public NoStoreCacheApiTests(PaymentsApiFactory factory)
    {
        _factory = factory.Seeded();
    }

    private static void ShouldNotBeCached(HttpResponseMessage response)
    {
        // HttpClient reordena las directivas al leerlas: se comparan una a una, no el texto.
        var cache = response.Headers.CacheControl!;
        cache.NoStore.Should().BeTrue();
        cache.NoCache.Should().BeTrue();
        cache.MustRevalidate.Should().BeTrue();
        cache.MaxAge.Should().Be(TimeSpan.Zero);
        response.Headers.Pragma.ToString().Should().Be("no-cache");
    }

    [Fact]
    public async Task Wallet_ShouldNotBeCached()
    {
        var response = await _factory.ClientWithToken(_factory.Token(Guid.NewGuid(), "ROLE_CLIENT"))
            .GetAsync("/api/payments/wallets/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldNotBeCached(response);
    }

    [Fact]
    public async Task ErrorsAndUnauthorized_ShouldNotBeCachedEither()
    {
        ShouldNotBeCached(await _factory.CreateClient().GetAsync("/api/payments/wallets/me"));
        ShouldNotBeCached(await _factory.ClientWithToken(_factory.Token(Guid.NewGuid(), "ROLE_CLIENT"))
            .GetAsync($"/api/payments/{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task InternalRoutes_ShouldNotBeCached()
    {
        ShouldNotBeCached(await _factory.InternalClient().GetAsync($"/internal/payments/{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task Health_ShouldKeepItsOwnHeaders()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        response.Headers.Pragma.Should().BeEmpty();
    }
}
