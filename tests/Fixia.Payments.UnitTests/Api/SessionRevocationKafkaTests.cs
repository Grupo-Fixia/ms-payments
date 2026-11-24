using System.Text.Json;
using Confluent.Kafka;
using Fixia.Payments.Api.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>
/// El consumidor de revocaciones (GC-547, fase 2) contra un Kafka real. Se omite sin <c>FIXIA_TEST_KAFKA</c> (por
/// ejemplo <c>localhost:29092</c>, el puerto externo del compose). Usa un tópico desechable.
/// </summary>
public class SessionRevocationKafkaTests
{
    public const string EnvironmentVariable = "FIXIA_TEST_KAFKA";

    private static string RevokedEvent(string jti) => JsonSerializer.Serialize(new
    {
        eventId = Guid.NewGuid(),
        eventType = "sesion.revocada",
        schemaVersion = 1,
        data = new { jti, expiresAt = "2999-01-01T00:00:00Z", revokedAt = "2026-11-22T00:00:00Z" }
    });

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [SkippableFact]
    public async Task RecuperaLoAnteriorQuedaAlDiaYVeLasRevocacionesNuevas()
    {
        var servers = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Skip.If(string.IsNullOrWhiteSpace(servers), $"Sin {EnvironmentVariable}: se omite la prueba con Kafka");

        var topic = $"gc547-smoke-{Guid.NewGuid():N}";
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = servers }).Build();
        var before = Guid.NewGuid().ToString();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = before, Value = RevokedEvent(before) });

        var options = Options.Create(new SessionRevocationOptions { Topic = topic, MaxStalenessSeconds = 5 });
        var list = new RevocationList(TimeProvider.System, options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kafka:BootstrapServers"] = servers })
            .Build();
        using var consumer = new SessionRevocationConsumer(list, options, TimeProvider.System, configuration,
            NullLogger<SessionRevocationConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => list.IsFresh, TimeSpan.FromSeconds(30));
            list.IsFresh.Should().BeTrue();
            list.Contains(before).Should().BeTrue("se relee lo publicado antes de arrancar");

            var after = Guid.NewGuid().ToString();
            await producer.ProduceAsync(topic, new Message<string, string> { Key = after, Value = "{no es json" });
            await producer.ProduceAsync(topic, new Message<string, string> { Key = after, Value = RevokedEvent(after) });
            await Until(() => list.Contains(after), TimeSpan.FromSeconds(10));
            list.Contains(after).Should().BeTrue();
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }
}
