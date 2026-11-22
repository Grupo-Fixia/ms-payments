using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>
/// Dinero retenido de un prestador (GC-524, contrato <c>ms-users/docs/contracts/empresas.md</c> §6.2): ms-users lo
/// consulta antes de dejar que un técnico se desvincule de su empresa. Cada prueba usa su propio prestador.
/// </summary>
public class HeldPaymentsApiTests : IClassFixture<PaymentsApiFactory>
{
    private const string Path = "/internal/payments/held";

    private readonly PaymentsApiFactory _factory;

    public HeldPaymentsApiTests(PaymentsApiFactory factory)
    {
        _factory = factory.Seeded();
    }

    private enum Kind { Pending, Held, Captured, Released, Failed, Gateway }

    /// <summary>Un pago del prestador sobre una solicitud nueva, en el estado pedido, armado como en GC-487.</summary>
    private async Task<Guid> GivenPaymentAsync(Kind kind, Guid providerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var now = DateTime.UtcNow;
        var clientId = Guid.NewGuid();
        var serviceRequestId = Guid.NewGuid();

        if (kind == Kind.Gateway)
        {
            var gateway = new Payment(serviceRequestId, clientId, providerId, 50000m, "COP", PaymentMethod.CreditCard);
            gateway.MarkAsProcessing("GW-1");
            db.Payments.Add(gateway);
            await db.SaveChangesAsync();
            return serviceRequestId;
        }

        var rule = await db.CommissionRules.SingleAsync(r => r.Id == PaymentsDbContext.InitialCommissionRuleId);
        var client = db.Wallets.Add(Wallet.OpenForUser(clientId, now)).Entity;
        var provider = await db.Wallets.SingleOrDefaultAsync(w => w.OwnerId == providerId)
                       ?? db.Wallets.Add(Wallet.OpenForUser(providerId, now)).Entity;
        var platform = await db.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId);

        var payment = Payment.ForContract(serviceRequestId, Guid.NewGuid(), clientId, providerId,
            rule.Quote(105000m, "COP"));
        db.Payments.Add(payment);
        if (kind == Kind.Failed)
            payment.MarkAsFailed("Saldo insuficiente");
        if (kind is Kind.Held or Kind.Captured or Kind.Released)
        {
            db.WalletEntries.Add(client.Apply(WalletEntryType.TopUp, 120750m, now));
            db.WalletEntries.Add(client.Apply(WalletEntryType.Hold, 120750m, now, payment.Id));
            payment.MarkAsProcessing("HOLD-1");
        }
        if (kind == Kind.Captured)
        {
            db.WalletEntries.Add(client.Apply(WalletEntryType.Capture, 120750m, now, payment.Id));
            db.WalletEntries.Add(provider.Apply(WalletEntryType.Credit, 105000m, now, payment.Id));
            db.WalletEntries.Add(platform.Apply(WalletEntryType.Commission, 15750m, now, payment.Id));
            payment.MarkAsCompleted("CAPTURE-1");
        }
        if (kind == Kind.Released)
        {
            db.WalletEntries.Add(client.Apply(WalletEntryType.Release, 120750m, now, payment.Id, "REF-1"));
            payment.ReleaseHold("REF-1");
        }

        await db.SaveChangesAsync();
        return serviceRequestId;
    }

    private Task<HttpResponseMessage> HeldAsync(object body, string? apiKey = PaymentsApiFactory.InternalApiKey) =>
        (apiKey is null ? _factory.CreateClient() : _factory.InternalClient(apiKey)).PostAsJsonAsync(Path, body);

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return json["heldServiceRequestIds"]!.AsArray().Select(n => Guid.Parse(n!.GetValue<string>())).ToList();
    }

    [Fact]
    public async Task SoloCuentanLosPagosPorRetenerOConLaRetencionHecha()
    {
        var provider = Guid.NewGuid();
        var pending = await GivenPaymentAsync(Kind.Pending, provider);
        var held = await GivenPaymentAsync(Kind.Held, provider);
        var captured = await GivenPaymentAsync(Kind.Captured, provider);
        var released = await GivenPaymentAsync(Kind.Released, provider);
        var failed = await GivenPaymentAsync(Kind.Failed, provider);
        var gateway = await GivenPaymentAsync(Kind.Gateway, provider);

        var ids = await IdsAsync(await HeldAsync(new
        {
            providerId = provider,
            serviceRequestIds = new[] { pending, held, captured, released, failed, gateway, Guid.NewGuid() }
        }));

        ids.Should().BeEquivalentTo(new[] { pending, held });
    }

    [Fact]
    public async Task SoloMiraLasSolicitudesPedidasYDeEsePrestador()
    {
        var provider = Guid.NewGuid();
        var asked = await GivenPaymentAsync(Kind.Held, provider);
        await GivenPaymentAsync(Kind.Held, provider);
        var otherProvider = await GivenPaymentAsync(Kind.Held, Guid.NewGuid());

        var ids = await IdsAsync(await HeldAsync(new
        {
            providerId = provider,
            serviceRequestIds = new[] { asked, otherProvider, asked }
        }));

        ids.Should().Equal(asked);
    }

    [Fact]
    public async Task SinSolicitudesNoHayNadaRetenido()
    {
        (await IdsAsync(await HeldAsync(new { providerId = Guid.NewGuid(), serviceRequestIds = Array.Empty<Guid>() })))
            .Should().BeEmpty();
        (await IdsAsync(await HeldAsync(new { providerId = Guid.NewGuid() }))).Should().BeEmpty();
    }

    [Fact]
    public async Task UnaConsultaMalFormadaOConDemasiadasSolicitudesResponde400()
    {
        (await HeldAsync(new { serviceRequestIds = new[] { Guid.NewGuid() } })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await HeldAsync(new { providerId = Guid.NewGuid(), serviceRequestIds = new[] { Guid.Empty } })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        var tooMany = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        (await HeldAsync(new { providerId = Guid.NewGuid(), serviceRequestIds = tooMany })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SoloConLaClaveInternaYNuncaConUnTokenDeUsuario()
    {
        var body = new { providerId = Guid.NewGuid(), serviceRequestIds = Array.Empty<Guid>() };
        (await HeldAsync(body, apiKey: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await HeldAsync(body, apiKey: "otra-clave")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = _factory.ClientWithToken(_factory.Token(Guid.NewGuid(), "ROLE_COMPANY"));
        (await user.PostAsJsonAsync(Path, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
