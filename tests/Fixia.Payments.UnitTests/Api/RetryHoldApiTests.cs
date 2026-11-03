using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>Reintento de la retención después de recargar (GC-484): <c>POST /api/payments/{id}/process</c>.</summary>
public class RetryHoldApiTests : IClassFixture<PaymentsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PaymentsApiFactory _factory;

    public RetryHoldApiTests(PaymentsApiFactory factory)
    {
        _factory = factory.Seeded();
    }

    /// <summary>Un pago con billetera cuya retención falló por saldo, como lo deja GC-484.</summary>
    private async Task<(Guid PaymentId, Guid ClientId, Guid ProviderId)> GivenFailedHoldAsync(decimal clientBalance)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var rule = await db.CommissionRules.SingleAsync(r => r.Id == PaymentsDbContext.InitialCommissionRuleId);
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var payment = Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), clientId, providerId, rule.Quote(105000m, "COP"));
        payment.MarkAsFailed("Saldo disponible insuficiente");
        db.Payments.Add(payment);
        var wallet = Wallet.OpenForUser(clientId, DateTime.UtcNow);
        db.Wallets.Add(wallet);
        if (clientBalance > 0)
            db.WalletEntries.Add(wallet.Apply(WalletEntryType.TopUp, clientBalance, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return (payment.Id, clientId, providerId);
    }

    private Task<HttpResponseMessage> ProcessAsync(Guid paymentId, Guid userId, string role = "ROLE_CLIENT") =>
        _factory.ClientWithToken(_factory.Token(userId, role))
            .PostAsJsonAsync($"/api/payments/{paymentId}/process", new ProcessPaymentRequest(), Json);

    [Fact]
    public async Task Retry_AfterTopUp_ShouldHoldTheTotal()
    {
        var (paymentId, clientId, _) = await GivenFailedHoldAsync(150000m);

        var response = await ProcessAsync(paymentId, clientId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PaymentResponse>(Json))!.Status.Should().Be(PaymentStatus.Processing);
        var balance = await _factory.ClientWithToken(_factory.Token(clientId, "ROLE_CLIENT"))
            .GetFromJsonAsync<WalletResponse>("/api/payments/wallets/me", Json);
        balance!.AvailableBalance.Should().Be(29250m);
        balance.HeldBalance.Should().Be(120750m);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.OutboxEvents.CountAsync(e => e.AggregateId == paymentId && e.EventType == "pago.procesado")).Should().Be(1);
    }

    [Fact]
    public async Task Retry_StillWithoutBalance_ShouldReturn409AndStayFailed()
    {
        var (paymentId, clientId, _) = await GivenFailedHoldAsync(1000m);

        (await ProcessAsync(paymentId, clientId)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.Payments.SingleAsync(p => p.Id == paymentId)).Status.Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public async Task Retry_OfAnAlreadyHeldPayment_ShouldReturn409()
    {
        var (paymentId, clientId, _) = await GivenFailedHoldAsync(300000m);
        (await ProcessAsync(paymentId, clientId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ProcessAsync(paymentId, clientId)).StatusCode.Should().Be(HttpStatusCode.Conflict, "no se retiene dos veces");
    }

    [Fact]
    public async Task Retry_ByTheProvider_ShouldReturn403()
    {
        var (paymentId, _, providerId) = await GivenFailedHoldAsync(300000m);

        (await ProcessAsync(paymentId, providerId, "ROLE_PROFESSIONAL")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
