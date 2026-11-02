using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>Recarga (GC-482) y retiro (GC-483) simulados de punta a punta, con Idempotency-Key.</summary>
public class WalletOperationsApiTests : IClassFixture<PaymentsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string TopUps = "/api/payments/wallets/me/top-ups";
    private const string Withdrawals = "/api/payments/wallets/me/withdrawals";

    private readonly PaymentsApiFactory _factory;

    public WalletOperationsApiTests(PaymentsApiFactory factory)
    {
        _factory = factory.Seeded();
    }

    private HttpClient As(Guid userId, string role) => _factory.ClientWithToken(_factory.Token(userId, role));

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        if (key != null)
            request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private async Task<WalletResponse> BalanceAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<WalletResponse>("/api/payments/wallets/me", Json))!;

    private async Task<int> EntriesAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var wallet = await db.Wallets.SingleAsync(w => w.OwnerId == userId);
        return await db.WalletEntries.CountAsync(e => e.WalletId == wallet.Id);
    }

    /// <summary>Un técnico con saldo: se le acredita directamente, como lo hará el cobro al cerrar (GC-485).</summary>
    private async Task<HttpClient> ProfessionalWithBalanceAsync(Guid userId, decimal available)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var wallet = Fixia.Payments.Domain.Entities.Wallet.OpenForUser(userId, DateTime.UtcNow);
            db.Wallets.Add(wallet);
            db.WalletEntries.Add(wallet.Apply(WalletEntryType.Credit, available, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        return As(userId, "ROLE_PROFESSIONAL");
    }

    [Fact]
    public async Task TopUp_ShouldIncreaseAvailableBalanceAndRecordTheEntry()
    {
        var userId = Guid.NewGuid();
        var client = As(userId, "ROLE_CLIENT");

        var response = await PostAsync(client, TopUps, new { amount = 150000.50m }, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
        var body = (await response.Content.ReadFromJsonAsync<WalletOperationResponse>(Json))!;
        body.Type.Should().Be(WalletEntryType.TopUp);
        body.Amount.Should().Be(150000.50m);
        body.AvailableBalance.Should().Be(150000.50m);
        body.GatewayReference.Should().StartWith("TOPUP-");
        (await BalanceAsync(client)).AvailableBalance.Should().Be(150000.50m);
        (await EntriesAsync(userId)).Should().Be(1);
    }

    [Fact]
    public async Task TopUp_RepeatedWithTheSameKeyAndBody_ShouldNotTopUpTwice()
    {
        var userId = Guid.NewGuid();
        var client = As(userId, "ROLE_CLIENT");
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(client, TopUps, new { amount = 50000 }, key);
        var retry = await PostAsync(client, TopUps, new { amount = 50000.00m, currency = "cop" }, key);

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.Headers.GetValues("Idempotent-Replayed").Should().ContainSingle("true");
        (await retry.Content.ReadFromJsonAsync<WalletOperationResponse>(Json))!.EntryId
            .Should().Be((await first.Content.ReadFromJsonAsync<WalletOperationResponse>(Json))!.EntryId);
        (await BalanceAsync(client)).AvailableBalance.Should().Be(50000m);
        (await EntriesAsync(userId)).Should().Be(1);
    }

    [Fact]
    public async Task TopUp_SameKeyWithAnotherAmount_ShouldReturn422AndChangeNothing()
    {
        var userId = Guid.NewGuid();
        var client = As(userId, "ROLE_CLIENT");
        var key = Guid.NewGuid().ToString();
        await PostAsync(client, TopUps, new { amount = 50000 }, key);

        var reused = await PostAsync(client, TopUps, new { amount = 70000 }, key);

        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await BalanceAsync(client)).AvailableBalance.Should().Be(50000m);
    }

    [Fact]
    public async Task IdempotencyKey_ShouldBeScopedToEachUser()
    {
        var key = Guid.NewGuid().ToString();
        var ana = As(Guid.NewGuid(), "ROLE_CLIENT");
        var luis = As(Guid.NewGuid(), "ROLE_CLIENT");

        (await PostAsync(ana, TopUps, new { amount = 1000 }, key)).StatusCode.Should().Be(HttpStatusCode.Created);
        var other = await PostAsync(luis, TopUps, new { amount = 2000 }, key);

        other.StatusCode.Should().Be(HttpStatusCode.Created);
        other.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
        (await BalanceAsync(luis)).AvailableBalance.Should().Be(2000m);
    }

    [Fact]
    public async Task TopUp_RejectedByTheGateway_ShouldReturn402AndChangeNothing()
    {
        // Que la clave quede libre tras el rechazo depende del rollback: lo prueba WalletOperationsPostgresTests.
        var userId = Guid.NewGuid();
        var client = As(userId, "ROLE_CLIENT");

        var rejected = await PostAsync(client, TopUps, new { amount = 10_000_000.01m }, Guid.NewGuid().ToString());

        rejected.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        (await BalanceAsync(client)).AvailableBalance.Should().Be(0m);
        (await EntriesAsync(userId)).Should().Be(0);
    }

    [Theory]
    [InlineData(null, 1000, HttpStatusCode.BadRequest)]
    [InlineData("", 1000, HttpStatusCode.BadRequest)]
    [InlineData("clave con espacios", 1000, HttpStatusCode.BadRequest)]
    [InlineData("k-1", 0, HttpStatusCode.BadRequest)]
    [InlineData("k-2", -10, HttpStatusCode.BadRequest)]
    [InlineData("k-3", 10.001, HttpStatusCode.BadRequest)]
    public async Task TopUp_WithInvalidKeyOrAmount_ShouldReturn400(string? key, decimal amount, HttpStatusCode expected)
    {
        var response = await PostAsync(As(Guid.NewGuid(), "ROLE_CLIENT"), TopUps, new { amount }, key);

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task TopUp_InAnotherCurrency_ShouldReturn400()
    {
        var response = await PostAsync(As(Guid.NewGuid(), "ROLE_CLIENT"), TopUps, new { amount = 10, currency = "USD" },
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("ROLE_PROFESSIONAL")]
    [InlineData("ROLE_COMPANY")]
    [InlineData("ROLE_ADMIN")]
    public async Task TopUp_ByAnyoneButAClient_ShouldReturn403(string role)
    {
        var response = await PostAsync(As(Guid.NewGuid(), role), TopUps, new { amount = 1000 }, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Operations_WithoutToken_ShouldReturn401()
    {
        var client = _factory.CreateClient();

        (await PostAsync(client, TopUps, new { amount = 1000 }, "k")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(client, Withdrawals, new { amount = 1000 }, "k")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Withdrawal_ShouldDecreaseAvailableBalanceOnce()
    {
        var userId = Guid.NewGuid();
        var client = await ProfessionalWithBalanceAsync(userId, 105000m);
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(client, Withdrawals, new { amount = 100000 }, key);
        var retry = await PostAsync(client, Withdrawals, new { amount = 100000 }, key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await first.Content.ReadFromJsonAsync<WalletOperationResponse>(Json))!;
        body.Type.Should().Be(WalletEntryType.Withdrawal);
        body.AvailableBalance.Should().Be(5000m);
        body.GatewayReference.Should().StartWith("PAYOUT-");
        retry.Headers.GetValues("Idempotent-Replayed").Should().ContainSingle("true");
        (await BalanceAsync(client)).AvailableBalance.Should().Be(5000m);
        (await EntriesAsync(userId)).Should().Be(2, "el abono inicial y un solo retiro");
    }

    [Fact]
    public async Task Withdrawal_AboveTheAvailableBalance_ShouldReturn409AndChangeNothing()
    {
        var userId = Guid.NewGuid();
        var client = await ProfessionalWithBalanceAsync(userId, 1000m);

        var response = await PostAsync(client, Withdrawals, new { amount = 1000.01m }, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BalanceAsync(client)).AvailableBalance.Should().Be(1000m);
    }

    [Fact]
    public async Task Withdrawal_ShouldNeverTouchTheHeldBalance()
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var wallet = Fixia.Payments.Domain.Entities.Wallet.OpenForUser(userId, DateTime.UtcNow);
            db.Wallets.Add(wallet);
            db.WalletEntries.Add(wallet.Apply(WalletEntryType.TopUp, 500m, DateTime.UtcNow));
            db.WalletEntries.Add(wallet.Apply(WalletEntryType.Hold, 400m, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var client = As(userId, "ROLE_COMPANY");
        var response = await PostAsync(client, Withdrawals, new { amount = 101 }, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "solo hay 100 disponibles; los 400 retenidos no se retiran");
        var balance = await BalanceAsync(client);
        balance.AvailableBalance.Should().Be(100m);
        balance.HeldBalance.Should().Be(400m);
    }

    [Theory]
    [InlineData("ROLE_CLIENT")]
    [InlineData("ROLE_ADMIN")]
    public async Task Withdrawal_ByAnyoneButATechnicianOrCompany_ShouldReturn403(string role)
    {
        var response = await PostAsync(As(Guid.NewGuid(), role), Withdrawals, new { amount = 10 }, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
