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

/// <summary>Billetera propia y cotización de comisión de punta a punta (GC-480, GC-481).</summary>
public class WalletsApiTests : IClassFixture<PaymentsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PaymentsApiFactory _factory;

    public WalletsApiTests(PaymentsApiFactory factory)
    {
        _factory = factory.Seeded();
    }

    private HttpClient As(Guid userId, string role) => _factory.ClientWithToken(_factory.Token(userId, role));

    /// <summary>Registra movimientos directamente en la base, como lo harán la recarga y la retención.</summary>
    private async Task<Wallet> GivenEntriesAsync(Guid userId, params (WalletEntryType Type, decimal Amount)[] moves)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.OwnerId == userId)
                     ?? db.Wallets.Add(Wallet.OpenForUser(userId, DateTime.UtcNow)).Entity;
        var at = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        foreach (var (type, amount) in moves)
        {
            at = at.AddMinutes(1);
            db.WalletEntries.Add(wallet.Apply(type, amount, at));
        }

        await db.SaveChangesAsync();
        return wallet;
    }

    [Fact]
    public async Task Wallet_WithoutToken_ShouldReturn401()
    {
        (await _factory.CreateClient().GetAsync("/api/payments/wallets/me")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.CreateClient().GetAsync("/api/payments/wallets/me/entries")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wallet_ForAnAdmin_ShouldReturn403()
    {
        var response = await As(Guid.NewGuid(), "ROLE_ADMIN").GetAsync("/api/payments/wallets/me");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("ROLE_CLIENT")]
    [InlineData("ROLE_PROFESSIONAL")]
    [InlineData("ROLE_COMPANY")]
    public async Task Wallet_FirstTime_ShouldBeCreatedAtZero_AndBeTheSameAfterwards(string role)
    {
        var userId = Guid.NewGuid();
        var client = As(userId, role);

        var first = await client.GetFromJsonAsync<WalletResponse>("/api/payments/wallets/me", Json);
        var second = await client.GetFromJsonAsync<WalletResponse>("/api/payments/wallets/me", Json);

        first!.AvailableBalance.Should().Be(0m);
        first.HeldBalance.Should().Be(0m);
        first.Currency.Should().Be("COP");
        second!.WalletId.Should().Be(first.WalletId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.Wallets.CountAsync(w => w.OwnerId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task Entries_ShouldBePagedFromNewestToOldest_AndReconcileWithTheBalance()
    {
        var userId = Guid.NewGuid();
        var wallet = await GivenEntriesAsync(userId,
            (WalletEntryType.TopUp, 200000m),
            (WalletEntryType.Hold, 120750m),
            (WalletEntryType.Release, 750m),
            (WalletEntryType.Capture, 120000m),
            (WalletEntryType.TopUp, 1000.50m));
        await GivenEntriesAsync(Guid.NewGuid(), (WalletEntryType.TopUp, 999m)); // de otro usuario: no debe aparecer
        var client = As(userId, "ROLE_CLIENT");

        var pages = new List<WalletEntriesPage>();
        string? cursor = null;
        do
        {
            var url = "/api/payments/wallets/me/entries?limit=2" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = (await client.GetFromJsonAsync<WalletEntriesPage>(url, Json))!;
            pages.Add(page);
            cursor = page.NextCursor;
        } while (cursor != null);

        pages.Select(p => p.Items.Count).Should().Equal(2, 2, 1);
        var all = pages.SelectMany(p => p.Items).ToList();
        all.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        all.Select(e => e.CreatedAt).Should().BeInDescendingOrder();
        all.Select(e => e.Type).Should().Equal(WalletEntryType.TopUp, WalletEntryType.Capture, WalletEntryType.Release,
            WalletEntryType.Hold, WalletEntryType.TopUp);

        var balance = await client.GetFromJsonAsync<WalletResponse>("/api/payments/wallets/me", Json);
        balance!.WalletId.Should().Be(wallet.Id);
        // 200000 − 120750 (retener) + 750 (liberar) + 1000,50 (recargar); el cobro solo baja lo retenido.
        balance.AvailableBalance.Should().Be(81000.50m).And.Be(all[0].AvailableAfter);
        balance.HeldBalance.Should().Be(0m).And.Be(all[0].HeldAfter);
        all.Sum(e => Wallet.Effect(e.Type, e.Amount).Available).Should().Be(balance.AvailableBalance);
    }

    [Fact]
    public async Task Entries_OfANewWallet_ShouldBeEmpty()
    {
        var page = await As(Guid.NewGuid(), "ROLE_PROFESSIONAL")
            .GetFromJsonAsync<WalletEntriesPage>("/api/payments/wallets/me/entries", Json);

        page!.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=51")]
    [InlineData("limit=abc")]
    [InlineData("cursor=no-es-un-cursor")]
    public async Task Entries_WithInvalidParameters_ShouldReturn400(string query)
    {
        var response = await As(Guid.NewGuid(), "ROLE_CLIENT").GetAsync($"/api/payments/wallets/me/entries?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task CommissionQuote_ShouldAddFifteenPercentOnTopOfThePrice()
    {
        var quote = await As(Guid.NewGuid(), "ROLE_CLIENT")
            .GetFromJsonAsync<CommissionQuoteResponse>("/api/payments/commission-quote?amount=105000", Json);

        quote!.Price.Should().Be(105000m);
        quote.Commission.Should().Be(15750m);
        quote.Total.Should().Be(120750m);
        quote.Rate.Should().Be(0.15m);
        quote.Currency.Should().Be("COP");
        quote.RuleId.Should().Be(PaymentsDbContext.InitialCommissionRuleId);
    }

    [Fact]
    public async Task CommissionQuote_WithoutToken_ShouldReturn401()
    {
        (await _factory.CreateClient().GetAsync("/api/payments/commission-quote?amount=1000")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?amount=abc")]
    [InlineData("?amount=-5")]
    [InlineData("?amount=0")]
    [InlineData("?amount=10.001")]
    [InlineData("?amount=1000&currency=USD")]
    public async Task CommissionQuote_WithInvalidAmount_ShouldReturn400(string query)
    {
        var response = await As(Guid.NewGuid(), "ROLE_CLIENT").GetAsync($"/api/payments/commission-quote{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
