using System.Text.Json;
using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Inbox;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Fixia.Payments.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixia.Payments.UnitTests.Application;

/// <summary>Retener al contratar (GC-484) y cobrar al cerrar (GC-485) a partir de los eventos de ms-services.</summary>
public class ServiceEventsHandlerTests
{
    private readonly string _database = $"events-{Guid.NewGuid()}";
    private readonly Guid _serviceRequestId = Guid.NewGuid();
    private readonly Guid _contractId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _providerId = Guid.NewGuid();

    public ServiceEventsHandlerTests()
    {
        using var db = NewContext();
        db.Database.EnsureCreated(); // siembra la cuenta de Fixia y la tarifa de 15 %
    }

    private PaymentsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(_database).Options);

    /// <summary>Cada evento con su propio contexto, como cada mensaje del consumidor.</summary>
    private async Task<bool> HandleAsync(ServiceEvent serviceEvent)
    {
        await using var db = NewContext();
        var handler = new ServiceEventsHandler(
            new PaymentRepository(db),
            new WalletRepository(db),
            new CommissionService(new CommissionRuleRepository(db), TimeProvider.System),
            new OutboxWriter(db),
            new EfProcessedEventStore(db),
            new UnitOfWork(db),
            TimeProvider.System,
            NullLogger<ServiceEventsHandler>.Instance);
        return await handler.HandleAsync(serviceEvent);
    }

    /// <summary>Un sobre como los de ms-services (docs/events/examples/flujo-completo).</summary>
    private ServiceEvent Event(string type, object data, Guid? eventId = null, string role = "CLIENT") =>
        ServiceEvent.Parse(JsonSerializer.Serialize(new
        {
            eventId = eventId ?? Guid.NewGuid(),
            eventType = type,
            schemaVersion = 1,
            occurredAt = "2026-11-02T17:45:00Z",
            producer = "ms-services",
            aggregate = new { type = "ServiceRequest", id = _serviceRequestId, sequence = 8 },
            correlationId = _serviceRequestId,
            actor = new { userId = _clientId, role },
            data
        }));

    private ServiceEvent Contracted(Guid? eventId = null) => Event(ServiceEvent.Contracted, new
    {
        contractId = _contractId,
        serviceRequestId = _serviceRequestId,
        quotationId = Guid.NewGuid(),
        clientId = _clientId,
        providerId = _providerId,
        price = new { amount = "105000.00", currency = "COP" },
        scope = "Cambio de la tubería",
        conditions = "Materiales incluidos",
        agreedAt = "2026-11-03T15:00:00Z",
        contractedAt = "2026-11-02T17:45:00Z",
        status = "CONTRACTED"
    }, eventId);

    private ServiceEvent Closed(Guid? eventId = null) => Event(ServiceEvent.Closed, new
    {
        contractId = _contractId,
        serviceRequestId = _serviceRequestId,
        clientId = _clientId,
        providerId = _providerId,
        closedAt = "2026-11-04T10:00:00Z",
        status = "CLOSED"
    }, eventId);

    private async Task GivenClientBalanceAsync(decimal amount)
    {
        await using var db = NewContext();
        var wallet = Wallet.OpenForUser(_clientId, DateTime.UtcNow);
        db.Wallets.Add(wallet);
        db.WalletEntries.Add(wallet.Apply(WalletEntryType.TopUp, amount, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<Wallet?> WalletOfAsync(Guid owner)
    {
        await using var db = NewContext();
        return await db.Wallets.AsNoTracking().SingleOrDefaultAsync(w => w.OwnerId == owner);
    }

    private async Task<Payment> PaymentAsync()
    {
        await using var db = NewContext();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.ServiceRequestId == _serviceRequestId);
    }

    private async Task<List<(string Type, long Sequence)>> PaymentEventsAsync(Guid paymentId)
    {
        await using var db = NewContext();
        return (await db.OutboxEvents.Where(e => e.AggregateId == paymentId).OrderBy(e => e.AggregateSequence)
                .Select(e => new { e.EventType, e.AggregateSequence }).ToListAsync())
            .Select(e => (e.EventType, e.AggregateSequence)).ToList();
    }

    [Fact]
    public async Task Contracted_WithEnoughBalance_ShouldCreateThePaymentAndHoldPricePlusCommission()
    {
        await GivenClientBalanceAsync(200000m);

        (await HandleAsync(Contracted())).Should().BeTrue();

        var payment = await PaymentAsync();
        payment.Status.Should().Be(PaymentStatus.Processing, "con billetera, Processing es retenido");
        payment.BaseAmount.Should().Be(105000m);
        payment.CommissionAmount.Should().Be(15750m);
        payment.Amount.Should().Be(120750m);
        payment.ContractId.Should().Be(_contractId);
        payment.CommissionRuleId.Should().Be(PaymentsDbContext.InitialCommissionRuleId);

        var client = await WalletOfAsync(_clientId);
        client!.AvailableBalance.Should().Be(79250m);
        client.HeldBalance.Should().Be(120750m);

        (await PaymentEventsAsync(payment.Id)).Should().Equal(("pago.creado", 1L), ("pago.procesado", 2L));
    }

    [Theory]
    [InlineData(50000)]
    [InlineData(0)]
    public async Task Contracted_WithoutEnoughBalance_ShouldFailWithoutMovingMoney(decimal balance)
    {
        if (balance > 0)
            await GivenClientBalanceAsync(balance);

        await HandleAsync(Contracted());

        var payment = await PaymentAsync();
        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.FailureReason.Should().Contain("insuficiente");
        (await WalletOfAsync(_clientId))?.HeldBalance.Should().Be(0m);
        (await PaymentEventsAsync(payment.Id)).Should().Equal(("pago.creado", 1L), ("pago.fallido", 2L));
    }

    [Fact]
    public async Task Contracted_DeliveredTwice_ShouldHoldOnlyOnce()
    {
        await GivenClientBalanceAsync(300000m);
        var eventId = Guid.NewGuid();

        (await HandleAsync(Contracted(eventId))).Should().BeTrue();
        (await HandleAsync(Contracted(eventId))).Should().BeFalse("es una reentrega del mismo eventId");
        (await HandleAsync(Contracted())).Should().BeTrue("otro eventId para la misma solicitud no crea otro pago");

        await using var db = NewContext();
        (await db.Payments.CountAsync(p => p.ServiceRequestId == _serviceRequestId)).Should().Be(1);
        (await WalletOfAsync(_clientId))!.HeldBalance.Should().Be(120750m);
    }

    [Fact]
    public async Task Closed_AfterHolding_ShouldPayTheProviderTheFullPriceAndFixiaTheCommission()
    {
        await GivenClientBalanceAsync(120750m);
        await HandleAsync(Contracted());
        var platformBefore = (await WalletOfAsync(Wallet.PlatformOwnerId))!.AvailableBalance;
        var closed = Closed();

        (await HandleAsync(closed)).Should().BeTrue();
        (await HandleAsync(closed)).Should().BeFalse("una reentrega no cobra dos veces");

        var payment = await PaymentAsync();
        payment.Status.Should().Be(PaymentStatus.Completed);
        var client = await WalletOfAsync(_clientId);
        client!.AvailableBalance.Should().Be(0m);
        client.HeldBalance.Should().Be(0m);
        (await WalletOfAsync(_providerId))!.AvailableBalance.Should().Be(105000m);
        (await WalletOfAsync(Wallet.PlatformOwnerId))!.AvailableBalance.Should().Be(platformBefore + 15750m);
        (await PaymentEventsAsync(payment.Id)).Select(e => e.Type)
            .Should().Equal("pago.creado", "pago.procesado", "pago.completado");
    }

    [Fact]
    public async Task ClosureObjected_ShouldKeepTheMoneyHeld()
    {
        await GivenClientBalanceAsync(120750m);
        await HandleAsync(Contracted());

        (await HandleAsync(Event(ServiceEvent.ClosureObjected, new
        {
            contractId = _contractId, serviceRequestId = _serviceRequestId, clientId = _clientId,
            providerId = _providerId, objectedAt = "2026-11-04T10:00:00Z", status = "IN_PROGRESS"
        }))).Should().BeTrue();

        (await PaymentAsync()).Status.Should().Be(PaymentStatus.Processing);
        (await WalletOfAsync(_clientId))!.HeldBalance.Should().Be(120750m);
        (await WalletOfAsync(_providerId)).Should().BeNull("el prestador no recibe nada mientras haya objeción");
    }

    [Fact]
    public async Task Closed_WithoutAHeldPayment_ShouldNotCreditAnyone()
    {
        await HandleAsync(Contracted()); // sin saldo: queda Failed

        await HandleAsync(Closed());

        (await PaymentAsync()).Status.Should().Be(PaymentStatus.Failed);
        (await WalletOfAsync(_providerId)).Should().BeNull();
    }

    [Fact]
    public async Task OtherServiceEvents_ShouldBeIgnored()
    {
        (await HandleAsync(Event("solicitud.publicada", new { serviceRequestId = _serviceRequestId }))).Should().BeFalse();

        await using var db = NewContext();
        (await db.ProcessedEvents.CountAsync()).Should().Be(0);
        (await db.Payments.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("{\"eventType\":\"servicio.contratado\"}")]
    public void Parse_OfAMessageOutsideTheContract_ShouldBeRejected(string message)
    {
        FluentActions.Invoking(() => ServiceEvent.Parse(message)).Should().Throw<InvalidServiceEventException>();
    }

    [Fact]
    public async Task Contracted_WithoutPrice_ShouldBeRejectedAsInvalid()
    {
        var invalid = Event(ServiceEvent.Contracted, new
        {
            contractId = _contractId, serviceRequestId = _serviceRequestId, clientId = _clientId, providerId = _providerId
        });

        await FluentActions.Invoking(() => HandleAsync(invalid)).Should().ThrowAsync<InvalidServiceEventException>();
    }
}
