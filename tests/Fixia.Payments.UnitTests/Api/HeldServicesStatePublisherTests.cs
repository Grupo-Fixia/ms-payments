using System.Text.Json;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Infrastructure.Messaging;
using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>
/// Estado de retención replicado (GC-547): por cada solicitud pagada con billetera se publica si el dinero sigue retenido,
/// solo cuando cambia, para que ms-users decida la desvinculación sin llamar a <c>/internal/payments/held</c>.
/// </summary>
public class HeldServicesStatePublisherTests : IDisposable
{
    private readonly PaymentsApiFactory _factory = new PaymentsApiFactory().Seeded();

    public void Dispose() => _factory.Dispose();

    private async Task<Payment> GivenWalletPaymentAsync(Guid providerId, bool held)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var rule = await db.CommissionRules.SingleAsync(r => r.Id == PaymentsDbContext.InitialCommissionRuleId);
        var payment = Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), providerId,
            rule.Quote(105000m, "COP"));
        if (held)
            payment.MarkAsProcessing("HOLD-1");
        else
            payment.MarkAsFailed("Saldo insuficiente");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private List<JsonElement> Published()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return db.OutboxEvents.AsNoTracking()
            .Where(e => e.EventType == HeldServicesStatePublisher.StateType)
            .AsEnumerable()
            .Select(e => JsonDocument.Parse(e.Payload).RootElement.Clone())
            .ToList();
    }

    [Fact]
    public async Task PublicaLaRetencionDeCadaSolicitudSoloCuandoCambia()
    {
        var provider = Guid.NewGuid();
        var held = await GivenWalletPaymentAsync(provider, held: true);
        var failed = await GivenWalletPaymentAsync(provider, held: false);
        var publisher = _factory.Services.GetRequiredService<HeldServicesStatePublisher>();

        (await publisher.PublishChangesAsync()).Should().Be(2);

        var states = Published();
        var heldState = states.Single(s =>
            s.GetProperty("aggregate").GetProperty("id").GetGuid() == held.ServiceRequestId);
        heldState.GetProperty("data").GetProperty("held").GetBoolean().Should().BeTrue();
        heldState.GetProperty("data").GetProperty("providerId").GetGuid().Should().Be(provider);
        heldState.GetProperty("removed").GetBoolean().Should().BeFalse();
        states.Single(s => s.GetProperty("aggregate").GetProperty("id").GetGuid() == failed.ServiceRequestId)
            .GetProperty("data").GetProperty("held").GetBoolean().Should().BeFalse();

        (await publisher.PublishChangesAsync()).Should().Be(0);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = await db.Payments.SingleAsync(p => p.Id == held.Id);
            payment.MarkAsCompleted("CAPTURE-1");
            await db.SaveChangesAsync();
        }

        (await publisher.PublishChangesAsync()).Should().Be(1);
        Published().Count(s => s.GetProperty("aggregate").GetProperty("id").GetGuid() == held.ServiceRequestId
                               && !s.GetProperty("data").GetProperty("held").GetBoolean())
            .Should().Be(1);
    }

    [Fact]
    public async Task ElMensajeVaAlTopicoDeEstadosConLaSolicitudComoClave()
    {
        var held = await GivenWalletPaymentAsync(Guid.NewGuid(), held: true);
        var publisher = _factory.Services.GetRequiredService<HeldServicesStatePublisher>();

        await publisher.PublishChangesAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var outbox = await db.OutboxEvents.AsNoTracking()
            .SingleAsync(e => e.EventType == HeldServicesStatePublisher.StateType);
        outbox.Topic.Should().Be("fixia.payments.held-services");
        outbox.MessageKey.Should().Be(held.ServiceRequestId.ToString());
    }
}
