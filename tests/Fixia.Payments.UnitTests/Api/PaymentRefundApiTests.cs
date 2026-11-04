using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>Liberación y reembolso autorizados de pagos con billetera (GC-487), de punta a punta.</summary>
public class PaymentRefundApiTests : IClassFixture<PaymentsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PaymentsApiFactory _factory;

    public PaymentRefundApiTests(PaymentsApiFactory factory)
    {
        _factory = factory.Seeded();
    }

    private sealed record Scenario(Guid PaymentId, Guid ClientId, Guid ProviderId);

    /// <summary>
    /// Un pago con billetera de 105000 + 15 % = 120750, retenido al cliente; si <paramref name="captured"/>, ya cobrado
    /// (prestador +105000, Fixia +15750). Se arma en la base como lo harán GC-484 y GC-485.
    /// </summary>
    private async Task<Scenario> GivenWalletPaymentAsync(bool captured, decimal providerWithdrew = 0m)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var now = DateTime.UtcNow;
        var rule = await db.CommissionRules.SingleAsync(r => r.Id == PaymentsDbContext.InitialCommissionRuleId);
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var client = Wallet.OpenForUser(clientId, now);
        var provider = Wallet.OpenForUser(providerId, now);
        db.Wallets.AddRange(client, provider);
        var platform = await db.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId);

        var payment = Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), clientId, providerId, rule.Quote(105000m, "COP"));
        db.Payments.Add(payment);
        db.WalletEntries.Add(client.Apply(WalletEntryType.TopUp, 120750m, now));
        db.WalletEntries.Add(client.Apply(WalletEntryType.Hold, 120750m, now, payment.Id));
        payment.MarkAsProcessing("HOLD-1");

        if (captured)
        {
            db.WalletEntries.Add(client.Apply(WalletEntryType.Capture, 120750m, now, payment.Id));
            db.WalletEntries.Add(provider.Apply(WalletEntryType.Credit, 105000m, now, payment.Id));
            db.WalletEntries.Add(platform.Apply(WalletEntryType.Commission, 15750m, now, payment.Id));
            payment.MarkAsCompleted("CAPTURE-1");
            if (providerWithdrew > 0)
                db.WalletEntries.Add(provider.Apply(WalletEntryType.Withdrawal, providerWithdrew, now));
        }

        await db.SaveChangesAsync();
        return new Scenario(payment.Id, clientId, providerId);
    }

    private async Task<(Wallet Client, Wallet Provider, Wallet Platform)> WalletsAsync(Scenario s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return (await db.Wallets.AsNoTracking().SingleAsync(w => w.OwnerId == s.ClientId),
            await db.Wallets.AsNoTracking().SingleAsync(w => w.OwnerId == s.ProviderId),
            await db.Wallets.AsNoTracking().SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId));
    }

    private async Task<List<JsonNode>> RefundEventsAsync(Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return (await db.OutboxEvents.Where(e => e.AggregateId == paymentId && e.EventType == "pago.reembolsado")
                .Select(e => e.Payload).ToListAsync())
            .Select(p => JsonNode.Parse(p)!).ToList();
    }

    private Task<HttpResponseMessage> RefundAsync(Guid paymentId, string? reason = "Objeción resuelta a favor del cliente",
        string? key = null, string role = "ROLE_ADMIN", Guid? userId = null)
    {
        var client = _factory.ClientWithToken(_factory.Token(userId ?? Guid.NewGuid(), role));
        var url = $"/api/payments/{paymentId}/refund" + (reason is null ? "" : $"?reason={Uri.EscapeDataString(reason)}");
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (key != "")
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Refund_OfAHeldPayment_ShouldReleaseTheTotalToTheClient()
    {
        var s = await GivenWalletPaymentAsync(captured: false);

        var response = await RefundAsync(s.PaymentId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PaymentResponse>(Json))!.Status.Should().Be(PaymentStatus.Refunded);
        var (client, provider, _) = await WalletsAsync(s);
        client.AvailableBalance.Should().Be(120750m);
        client.HeldBalance.Should().Be(0m);
        provider.AvailableBalance.Should().Be(0m, "no se había cobrado: el prestador no recibió nada");

        var refund = (await RefundEventsAsync(s.PaymentId)).Should().ContainSingle().Subject;
        refund["data"]!["kind"]!.GetValue<string>().Should().Be("RELEASE");
        refund["actor"]!["role"]!.GetValue<string>().Should().Be("ADMIN");
    }

    [Fact]
    public async Task Refund_OfACapturedPayment_ShouldReverseEveryCredit()
    {
        var s = await GivenWalletPaymentAsync(captured: true);
        var platformBefore = (await WalletsAsync(s)).Platform.AvailableBalance;

        var response = await RefundAsync(s.PaymentId, "Disputa resuelta a favor del cliente");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (client, provider, platform) = await WalletsAsync(s);
        client.AvailableBalance.Should().Be(120750m);
        provider.AvailableBalance.Should().Be(0m);
        platform.AvailableBalance.Should().Be(platformBefore - 15750m);

        (await RefundEventsAsync(s.PaymentId)).Single()["data"]!["kind"]!.GetValue<string>().Should().Be("REVERSAL");
    }

    [Fact]
    public async Task Refund_WhenTheProviderAlreadyWithdrew_ShouldReturn409AndChangeNothing()
    {
        var s = await GivenWalletPaymentAsync(captured: true, providerWithdrew: 100000m);

        var response = await RefundAsync(s.PaymentId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var (client, provider, _) = await WalletsAsync(s);
        client.AvailableBalance.Should().Be(0m);
        provider.AvailableBalance.Should().Be(5000m);
        (await RefundEventsAsync(s.PaymentId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Refund_RepeatedWithTheSameKey_ShouldReturnTheSameResultWithoutRefundingTwice()
    {
        var s = await GivenWalletPaymentAsync(captured: false);
        var key = Guid.NewGuid().ToString();
        var admin = Guid.NewGuid(); // la clave es por usuario: el reintento es del mismo administrador

        var first = await RefundAsync(s.PaymentId, key: key, userId: admin);
        var retry = await RefundAsync(s.PaymentId, key: key, userId: admin);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.Headers.GetValues("Idempotent-Replayed").Should().ContainSingle("true");
        (await WalletsAsync(s)).Client.AvailableBalance.Should().Be(120750m);
        (await RefundEventsAsync(s.PaymentId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Refund_AgainWithANewKey_ShouldBeRejectedBecauseItIsAlreadyRefunded()
    {
        var s = await GivenWalletPaymentAsync(captured: false);
        await RefundAsync(s.PaymentId);

        var second = await RefundAsync(s.PaymentId);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await WalletsAsync(s)).Client.AvailableBalance.Should().Be(120750m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Refund_WithoutReason_ShouldReturn400(string? reason)
    {
        var s = await GivenWalletPaymentAsync(captured: false);

        (await RefundAsync(s.PaymentId, reason)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refund_WithoutIdempotencyKey_ShouldReturn400()
    {
        var s = await GivenWalletPaymentAsync(captured: false);

        (await RefundAsync(s.PaymentId, key: "")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("ROLE_CLIENT")]
    [InlineData("ROLE_PROFESSIONAL")]
    public async Task Refund_ByAnyoneButAnAdmin_ShouldReturn403(string role)
    {
        var s = await GivenWalletPaymentAsync(captured: false);

        (await RefundAsync(s.PaymentId, role: role)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await WalletsAsync(s)).Client.HeldBalance.Should().Be(120750m);
    }

    [Fact]
    public async Task Process_OfAWalletPayment_ShouldNotChargeThroughTheGateway()
    {
        var s = await GivenWalletPaymentAsync(captured: false);
        var client = _factory.ClientWithToken(_factory.Token(s.ClientId, "ROLE_CLIENT"));

        var response = await client.PostAsJsonAsync($"/api/payments/{s.PaymentId}/process", new ProcessPaymentRequest(), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
