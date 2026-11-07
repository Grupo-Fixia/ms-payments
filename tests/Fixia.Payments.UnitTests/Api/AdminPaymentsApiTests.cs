using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>
/// Consultas del ADMIN y su registro de acciones (GC-535, contrato <c>ms-users/docs/contracts/admin.md</c> §4 y §5).
/// Comparten la base de la clase: cada prueba filtra por sus propios usuarios.
/// </summary>
public class AdminPaymentsApiTests : IClassFixture<PaymentsApiFactory>
{
    private const string Payments = "/api/payments/admin/payments";
    private const string Audit = "/api/payments/admin/audit-events";

    private readonly PaymentsApiFactory _factory;

    public AdminPaymentsApiTests(PaymentsApiFactory factory)
    {
        _factory = factory.Seeded();
    }

    private enum Kind { Pending, Held, Captured, Released, Gateway }

    /// <summary>Un pago del cliente <paramref name="clientId"/> en el estado pedido, armado como en GC-487.</summary>
    private async Task<Payment> GivenPaymentAsync(Kind kind, Guid clientId, Guid providerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var now = DateTime.UtcNow;

        if (kind == Kind.Gateway)
        {
            var gateway = new Payment(Guid.NewGuid(), clientId, providerId, 50000m, "COP", PaymentMethod.CreditCard);
            gateway.MarkAsProcessing("GW-1");
            gateway.MarkAsCompleted("GW-1");
            db.Payments.Add(gateway);
            await db.SaveChangesAsync();
            return gateway;
        }

        var rule = await db.CommissionRules.SingleAsync(r => r.Id == PaymentsDbContext.InitialCommissionRuleId);
        var client = await db.Wallets.SingleOrDefaultAsync(w => w.OwnerId == clientId)
                     ?? db.Wallets.Add(Wallet.OpenForUser(clientId, now)).Entity;
        var provider = await db.Wallets.SingleOrDefaultAsync(w => w.OwnerId == providerId)
                       ?? db.Wallets.Add(Wallet.OpenForUser(providerId, now)).Entity;
        var platform = await db.Wallets.SingleAsync(w => w.OwnerId == Wallet.PlatformOwnerId);

        var payment = Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), clientId, providerId, rule.Quote(105000m, "COP"));
        db.Payments.Add(payment);
        if (kind != Kind.Pending)
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
        return payment;
    }

    private HttpClient As(string role, Guid? userId = null) =>
        _factory.ClientWithToken(_factory.Token(userId ?? Guid.NewGuid(), role));

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private async Task<List<AdminAuditEvent>> AuditOfAsync(Guid adminId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.AdminAuditEvents.AsNoTracking().Where(e => e.AdminId == adminId)
            .OrderBy(e => e.OccurredAt).ToListAsync();
    }

    // ---------- Acceso (§2) ----------

    [Fact]
    public async Task WithoutToken_ShouldReturn401()
    {
        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync(Payments)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(Audit)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("ROLE_CLIENT")]
    [InlineData("ROLE_PROFESSIONAL")]
    [InlineData("ROLE_COMPANY")]
    public async Task OtherRoles_ShouldReturn403AndLeaveNoRecord(string role)
    {
        var userId = Guid.NewGuid();
        var client = As(role, userId);

        (await client.GetAsync(Payments)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(Audit)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AuditOfAsync(userId)).Should().BeEmpty();
    }

    // ---------- Lista de pagos (§4.1) ----------

    [Fact]
    public async Task List_ShouldSayWhetherEachPaymentIsRefundableAndHow()
    {
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var pending = await GivenPaymentAsync(Kind.Pending, clientId, providerId);
        var held = await GivenPaymentAsync(Kind.Held, clientId, providerId);
        var captured = await GivenPaymentAsync(Kind.Captured, clientId, providerId);
        var released = await GivenPaymentAsync(Kind.Released, clientId, providerId);
        var gateway = await GivenPaymentAsync(Kind.Gateway, clientId, providerId);

        var response = await As("ROLE_ADMIN").GetAsync($"{Payments}?userId={clientId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await JsonAsync(response);
        var items = page["items"]!.AsArray().ToDictionary(i => i!["id"]!.GetValue<Guid>());
        items.Should().HaveCount(5);
        page["nextCursor"].Should().BeNull();

        void Expect(Payment p, string hold, bool refundable, string? kind)
        {
            var item = items[p.Id]!;
            item["holdStatus"]!.GetValue<string>().Should().Be(hold);
            item["refundable"]!.GetValue<bool>().Should().Be(refundable);
            item["refundKind"]?.GetValue<string>().Should().Be(kind);
            if (kind is null)
                item["refundKind"].Should().BeNull();
            item["breakdown"].Should().NotBeNull(hold == "NotApplicable" ? "" : "los pagos con billetera traen desglose");
        }

        Expect(pending, "Pending", false, null);
        Expect(held, "Held", true, "RELEASE");
        Expect(captured, "Captured", true, "REVERSAL");
        Expect(released, "Released", false, null);
        items[gateway.Id]!["refundable"]!.GetValue<bool>().Should().BeTrue();
        items[gateway.Id]!["refundKind"]!.GetValue<string>().Should().Be("GATEWAY");
    }

    [Fact]
    public async Task List_ShouldFilterByHoldStatusStatusServiceRequestAndUser()
    {
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var held = await GivenPaymentAsync(Kind.Held, clientId, providerId);
        var captured = await GivenPaymentAsync(Kind.Captured, clientId, providerId);
        await GivenPaymentAsync(Kind.Released, clientId, providerId);
        var admin = As("ROLE_ADMIN");

        async Task<List<Guid>> Ids(string query) =>
            (await JsonAsync(await admin.GetAsync($"{Payments}?{query}")))["items"]!.AsArray()
            .Select(i => i!["id"]!.GetValue<Guid>()).ToList();

        (await Ids($"userId={clientId}&holdStatus=held")).Should().Equal(held.Id);
        // userId también encuentra los pagos en que el usuario es el prestador.
        (await Ids($"userId={providerId}&holdStatus=Captured")).Should().Equal(captured.Id);
        (await Ids($"userId={clientId}&status=Completed")).Should().Equal(captured.Id);
        (await Ids($"serviceRequestId={held.ServiceRequestId}")).Should().Equal(held.Id);
        (await Ids($"userId={Guid.NewGuid()}")).Should().BeEmpty();
    }

    [Fact]
    public async Task List_ShouldPageWithTheCursorWithoutRepeatingOrSkipping()
    {
        var clientId = Guid.NewGuid();
        var expected = new List<Guid>();
        for (var i = 0; i < 5; i++)
            expected.Add((await GivenPaymentAsync(Kind.Held, clientId, Guid.NewGuid())).Id);
        var admin = As("ROLE_ADMIN");

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"{Payments}?userId={clientId}&limit=2" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = await JsonAsync(await admin.GetAsync(url));
            seen.AddRange(page["items"]!.AsArray().Select(i => i!["id"]!.GetValue<Guid>()));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        } while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        seen.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("status=Paid")]
    [InlineData("status=5")]
    [InlineData("holdStatus=Frozen")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("cursor=no-es-un-cursor")]
    public async Task List_WithAnInvalidParameter_ShouldReturn400AndLeaveNoRecord(string query)
    {
        var adminId = Guid.NewGuid();
        var response = await As("ROLE_ADMIN", adminId).GetAsync($"{Payments}?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AuditOfAsync(adminId)).Should().BeEmpty();
    }

    // ---------- Registro de acciones (§4.2 a §4.4 y §5) ----------

    [Fact]
    public async Task EveryAdminQuery_ShouldBeRecordedWithItsOutcome()
    {
        var adminId = Guid.NewGuid();
        var admin = As("ROLE_ADMIN", adminId);
        var payment = await GivenPaymentAsync(Kind.Held, Guid.NewGuid(), Guid.NewGuid());
        var missing = Guid.NewGuid();

        (await admin.GetAsync($"{Payments}?holdStatus=Held&limit=5")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/payments/{payment.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/payments/{missing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/payments/service-request/{payment.ServiceRequestId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var events = await AuditOfAsync(adminId);
        events.Select(e => (e.Action, e.TargetId, e.Outcome)).Should().Equal(
            (AdminAction.PAYMENT_LIST, (Guid?)null, AdminAuditOutcome.OK),
            (AdminAction.PAYMENT_VIEW, payment.Id, AdminAuditOutcome.OK),
            (AdminAction.PAYMENT_VIEW, missing, AdminAuditOutcome.NOT_FOUND),
            (AdminAction.PAYMENT_VIEW, payment.Id, AdminAuditOutcome.OK));
        events[0].Details.Should().Be("{\"holdStatus\":\"Held\",\"limit\":5}");
        events[3].Details.Should().Contain(payment.ServiceRequestId.ToString());
        events.Should().OnlyContain(e => e.TargetType == null || e.TargetType == "PAYMENT");
    }

    [Fact]
    public async Task OwnPaymentQueries_OfAClient_ShouldNotBeRecorded()
    {
        var clientId = Guid.NewGuid();
        var payment = await GivenPaymentAsync(Kind.Held, clientId, Guid.NewGuid());

        (await As("ROLE_CLIENT", clientId).GetAsync($"/api/payments/{payment.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await AuditOfAsync(clientId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Refund_ShouldBeRecordedOnceEvenIfRetriedAndFailuresWithTheirOutcome()
    {
        var adminId = Guid.NewGuid();
        var admin = As("ROLE_ADMIN", adminId);
        var payment = await GivenPaymentAsync(Kind.Held, Guid.NewGuid(), Guid.NewGuid());
        var key = Guid.NewGuid().ToString();

        Task<HttpResponseMessage> Refund(Guid id, string idempotencyKey, string? reason = "Objeción a favor del cliente")
        {
            var url = $"/api/payments/{id}/refund" + (reason is null ? "" : $"?reason={Uri.EscapeDataString(reason)}");
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            return admin.SendAsync(request);
        }

        (await Refund(payment.Id, key)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Refund(payment.Id, key)).Headers.GetValues("Idempotent-Replayed").Should().ContainSingle("true");
        (await Refund(payment.Id, Guid.NewGuid().ToString())).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var missing = Guid.NewGuid();
        (await Refund(missing, Guid.NewGuid().ToString())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Refund(payment.Id, Guid.NewGuid().ToString(), reason: null)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var refunds = (await AuditOfAsync(adminId)).Where(e => e.Action == AdminAction.PAYMENT_REFUND).ToList();
        refunds.Select(e => (e.TargetId, e.Outcome)).Should().Equal(
            (payment.Id, AdminAuditOutcome.OK),
            (payment.Id, AdminAuditOutcome.CONFLICT),
            (missing, AdminAuditOutcome.NOT_FOUND));
        var ok = JsonNode.Parse(refunds[0].Details!)!;
        ok["kind"]!.GetValue<string>().Should().Be("RELEASE");
        ok["reason"]!.GetValue<string>().Should().Be("Objeción a favor del cliente");
        ok["idempotencyKey"]!.GetValue<string>().Should().Be(key);
    }

    [Fact]
    public async Task AuditEvents_ShouldListNewestFirstWithoutIncludingThemselves()
    {
        var adminId = Guid.NewGuid();
        var admin = As("ROLE_ADMIN", adminId);
        await admin.GetAsync($"{Payments}?limit=1");
        await admin.GetAsync($"/api/payments/{Guid.NewGuid()}");

        var response = await admin.GetAsync($"{Audit}?adminId={adminId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await JsonAsync(response))["items"]!.AsArray();
        items.Select(i => i!["action"]!.GetValue<string>()).Should().Equal("PAYMENT_VIEW", "PAYMENT_LIST");
        items[0]!["outcome"]!.GetValue<string>().Should().Be("NOT_FOUND");
        items[1]!["details"]!["limit"]!.GetValue<int>().Should().Be(1, "los detalles salen como objeto JSON");

        var filtered = await JsonAsync(await admin.GetAsync($"{Audit}?adminId={adminId}&action=audit_list"));
        filtered["items"]!.AsArray().Should().HaveCount(1, "la primera consulta del registro ya quedó registrada");
        (await admin.GetAsync($"{Audit}?action=DELETE")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void RefundKindOf_ShouldFollowTheRefundRules()
    {
        var gatewayPending = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000m, "COP",
            PaymentMethod.CreditCard);
        AdminPaymentsService.RefundKindOf(gatewayPending).Should().BeNull("una pasarela sin cobrar no se reembolsa");
    }
}
