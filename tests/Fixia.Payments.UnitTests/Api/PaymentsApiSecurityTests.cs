using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>
/// Seguridad de la API de punta a punta (GC-477): token de ms-users, roles, dueño del pago y clave interna.
/// </summary>
public class PaymentsApiSecurityTests : IClassFixture<PaymentsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PaymentsApiFactory _factory;

    public PaymentsApiSecurityTests(PaymentsApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<PaymentResponse> CreatePaymentAsync(Guid clientId, Guid providerId, decimal amount = 120000m)
    {
        var response = await _factory.InternalClient().PostAsJsonAsync("/internal/payments",
            new CreatePaymentRequest(Guid.NewGuid(), clientId, providerId, amount, "COP", PaymentMethod.CreditCard), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>(Json))!;
    }

    [Theory]
    [InlineData("/api/payments/health")]
    [InlineData("/actuator/health")]
    [InlineData("/api/payments/swagger/v1/swagger.json")]
    public async Task PublicEndpoints_ShouldNotRequireToken(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_ShouldReturn401ProblemWithoutDetails()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().Be("No autenticado");
        body.RootElement.GetProperty("detail").GetString().Should().Be("Se requiere autenticación válida");
    }

    public static IEnumerable<object[]> InvalidTokens()
    {
        var foreignKey = RSA.Create(2048);
        var later = DateTime.UtcNow.AddMinutes(15);
        var user = $"usr_{Guid.NewGuid()}";
        yield return new object[] { "vencido", (Func<PaymentsApiFactory, string>)(f =>
            PaymentsApiFactory.CreateToken(user, new[] { "ROLE_CLIENT" }, PaymentsApiFactory.Issuer,
                PaymentsApiFactory.Audience, DateTime.UtcNow.AddMinutes(-1), f.SigningKey)) };
        yield return new object[] { "otro emisor", (Func<PaymentsApiFactory, string>)(f =>
            PaymentsApiFactory.CreateToken(user, new[] { "ROLE_CLIENT" }, "https://otro.emisor",
                PaymentsApiFactory.Audience, later, f.SigningKey)) };
        yield return new object[] { "otra audiencia", (Func<PaymentsApiFactory, string>)(f =>
            PaymentsApiFactory.CreateToken(user, new[] { "ROLE_CLIENT" }, PaymentsApiFactory.Issuer,
                "https://otra.audiencia", later, f.SigningKey)) };
        yield return new object[] { "firmado con otra clave", (Func<PaymentsApiFactory, string>)(_ =>
            PaymentsApiFactory.CreateToken(user, new[] { "ROLE_CLIENT" }, PaymentsApiFactory.Issuer,
                PaymentsApiFactory.Audience, later, foreignKey)) };
        yield return new object[] { "sub sin usr_", (Func<PaymentsApiFactory, string>)(f =>
            PaymentsApiFactory.CreateToken(Guid.NewGuid().ToString(), new[] { "ROLE_CLIENT" }, PaymentsApiFactory.Issuer,
                PaymentsApiFactory.Audience, later, f.SigningKey)) };
        yield return new object[] { "texto cualquiera", (Func<PaymentsApiFactory, string>)(_ => "no-es-un-jwt") };
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task ProtectedEndpoint_WithInvalidToken_ShouldReturn401(string caso, Func<PaymentsApiFactory, string> token)
    {
        var response = await _factory.ClientWithToken(token(_factory)).GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, caso);
    }

    [Fact]
    public async Task PublicCreateRoute_ShouldNoLongerExist()
    {
        var client = _factory.ClientWithToken(_factory.Token(Guid.NewGuid(), "ROLE_CLIENT"));

        var response = await client.PostAsJsonAsync("/api/payments",
            new CreatePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000m, "COP", PaymentMethod.CreditCard), Json);

        ((int)response.StatusCode).Should().BeOneOf(404, 405);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clave-equivocada")]
    public async Task InternalCreate_WithoutValidKey_ShouldReturn401(string? apiKey)
    {
        var client = apiKey is null ? _factory.CreateClient() : _factory.InternalClient(apiKey);

        var response = await client.PostAsJsonAsync("/internal/payments",
            new CreatePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000m, "COP", PaymentMethod.CreditCard), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InternalCreate_WithUserTokenInsteadOfKey_ShouldReturn401()
    {
        var client = _factory.ClientWithToken(_factory.Token(Guid.NewGuid(), "ROLE_ADMIN"));

        var response = await client.PostAsJsonAsync("/internal/payments",
            new CreatePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000m, "COP", PaymentMethod.CreditCard), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InternalCreate_ShouldBeIdempotent_AndRejectDifferentDataForTheSameRequest()
    {
        var request = new CreatePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 80000m, "COP", PaymentMethod.CreditCard);
        var client = _factory.InternalClient();

        var first = await client.PostAsJsonAsync("/internal/payments", request, Json);
        var retry = await client.PostAsJsonAsync("/internal/payments", request, Json);
        var different = await client.PostAsJsonAsync("/internal/payments", request with { Amount = 99000m }, Json);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Location!.ToString().Should().StartWith("/api/payments/");
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await retry.Content.ReadFromJsonAsync<PaymentResponse>(Json))!.Id
            .Should().Be((await first.Content.ReadFromJsonAsync<PaymentResponse>(Json))!.Id);
        different.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        (await db.Payments.CountAsync(p => p.ServiceRequestId == request.ServiceRequestId)).Should().Be(1);
        var paymentId = (await db.Payments.SingleAsync(p => p.ServiceRequestId == request.ServiceRequestId)).Id;
        (await db.OutboxEvents.CountAsync(e => e.AggregateId == paymentId)).Should().Be(1);
    }

    [Fact]
    public async Task Payment_ShouldBeVisibleOnlyToItsParticipantsAndAdmin()
    {
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var payment = await CreatePaymentAsync(clientId, providerId);

        async Task<HttpStatusCode> Get(string token, string path) =>
            (await _factory.ClientWithToken(token).GetAsync(path)).StatusCode;

        var byId = $"/api/payments/{payment.Id}";
        var byRequest = $"/api/payments/service-request/{payment.ServiceRequestId}";

        (await Get(_factory.Token(clientId, "ROLE_CLIENT"), byId)).Should().Be(HttpStatusCode.OK);
        (await Get(_factory.Token(providerId, "ROLE_PROFESSIONAL"), byId)).Should().Be(HttpStatusCode.OK);
        (await Get(_factory.Token(Guid.NewGuid(), "ROLE_ADMIN"), byId)).Should().Be(HttpStatusCode.OK);
        (await Get(_factory.Token(Guid.NewGuid(), "ROLE_CLIENT"), byId)).Should().Be(HttpStatusCode.NotFound);
        (await Get(_factory.Token(clientId, "ROLE_CLIENT"), byRequest)).Should().Be(HttpStatusCode.OK);
        (await Get(_factory.Token(Guid.NewGuid(), "ROLE_PROFESSIONAL"), byRequest)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Process_ShouldOnlyBeAllowedToThePayer()
    {
        var clientId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var payment = await CreatePaymentAsync(clientId, providerId);
        var path = $"/api/payments/{payment.Id}/process";

        var byProvider = await _factory.ClientWithToken(_factory.Token(providerId, "ROLE_PROFESSIONAL"))
            .PostAsJsonAsync(path, new ProcessPaymentRequest(), Json);
        var byStranger = await _factory.ClientWithToken(_factory.Token(Guid.NewGuid(), "ROLE_CLIENT"))
            .PostAsJsonAsync(path, new ProcessPaymentRequest(), Json);
        var byClient = await _factory.ClientWithToken(_factory.Token(clientId, "ROLE_CLIENT"))
            .PostAsJsonAsync(path, new ProcessPaymentRequest(), Json);

        byProvider.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        byStranger.StatusCode.Should().Be(HttpStatusCode.NotFound);
        byClient.StatusCode.Should().Be(HttpStatusCode.OK);
        (await byClient.Content.ReadFromJsonAsync<PaymentResponse>(Json))!.Status.Should().Be(PaymentStatus.Completed);
    }

    [Fact]
    public async Task Refund_ShouldOnlyBeAllowedToAdmin_AndLeaveTheEventsInTheOutbox()
    {
        var clientId = Guid.NewGuid();
        var payment = await CreatePaymentAsync(clientId, Guid.NewGuid());
        await _factory.ClientWithToken(_factory.Token(clientId, "ROLE_CLIENT"))
            .PostAsJsonAsync($"/api/payments/{payment.Id}/process", new ProcessPaymentRequest(), Json);
        var path = $"/api/payments/{payment.Id}/refund?reason=servicio%20cancelado";

        var byClient = await _factory.ClientWithToken(_factory.Token(clientId, "ROLE_CLIENT")).PostAsync(path, null);
        var byAdmin = await _factory.ClientWithToken(_factory.Token(Guid.NewGuid(), "ROLE_ADMIN")).PostAsync(path, null);

        byClient.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        byClient.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        byAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await byAdmin.Content.ReadFromJsonAsync<PaymentResponse>(Json))!.Status.Should().Be(PaymentStatus.Refunded);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var events = await db.OutboxEvents
            .Where(e => e.AggregateId == payment.Id)
            .OrderBy(e => e.AggregateSequence)
            .Select(e => e.EventType)
            .ToListAsync();
        events.Should().Equal("pago.creado", "pago.completado", "pago.reembolsado");
    }
}
