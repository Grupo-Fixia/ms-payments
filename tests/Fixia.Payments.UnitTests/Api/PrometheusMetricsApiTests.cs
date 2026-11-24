using System.Net;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Api;

public class PrometheusMetricsApiTests : IClassFixture<PaymentsApiFactory>
{
    private readonly PaymentsApiFactory _factory;

    public PrometheusMetricsApiTests(PaymentsApiFactory factory)
    {
        _factory = factory.Seeded();
    }

    [Fact]
    public async Task MetricsEndpoints_ShouldExposePrometheusTextWithNormalizedUri()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/health");
        await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        foreach (var path in new[] { "/metrics", "/actuator/prometheus" })
        {
            var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");

            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("http_server_requests_seconds_count");
            body.Should().Contain("uri=\"/health\"");
            body.Should().NotContain("uri=\"UNKNOWN\"");
        }
    }
}
