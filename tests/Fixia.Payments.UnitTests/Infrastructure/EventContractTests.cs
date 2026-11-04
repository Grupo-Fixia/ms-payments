using System.Text.Json.Nodes;
using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using FluentAssertions;
using Json.Schema;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Contrato de los eventos <c>pago.*</c> (GC-488): los ejemplos de <c>docs/events/examples</c> y los eventos que
/// realmente escribe el Outbox cumplen los esquemas de <c>docs/events/schemas</c>. Si alguien cambia un evento sin
/// actualizar el contrato (o al revés), esta prueba falla.
/// </summary>
public class EventContractTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonSchema>> Schemas = new(LoadSchemas);

    private static string EventsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "events", "schemas")))
            dir = dir.Parent;
        return dir == null
            ? throw new DirectoryNotFoundException("No se encontró docs/events/schemas desde la carpeta de las pruebas")
            : Path.Combine(dir.FullName, "docs", "events");
    }

    /// <summary>Registra todos los esquemas por su $id y devuelve los de cada tipo de evento.</summary>
    private static IReadOnlyDictionary<string, JsonSchema> LoadSchemas()
    {
        var byEventType = new Dictionary<string, JsonSchema>();
        foreach (var file in Directory.GetFiles(Path.Combine(EventsDirectory(), "schemas"), "*.schema.json"))
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file));
            var id = (JsonNode.Parse(File.ReadAllText(file))!["$id"] ?? throw new InvalidOperationException($"{file} sin $id"))
                .GetValue<string>();
            SchemaRegistry.Global.Register(new Uri(id), schema);

            var name = Path.GetFileName(file);
            if (name.StartsWith("pago.", StringComparison.Ordinal))
                byEventType[name[..name.IndexOf(".v1.", StringComparison.Ordinal)]] = schema;
        }

        return byEventType;
    }

    private static (bool Valid, string Errors) Validate(string json)
    {
        var node = JsonNode.Parse(json)!;
        var eventType = node["eventType"]!.GetValue<string>();
        Schemas.Value.Should().ContainKey(eventType, "cada tipo de evento debe tener su esquema");

        var result = Schemas.Value[eventType].Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var errors = string.Join("; ", (result.Details ?? new List<EvaluationResults>())
            .Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Key}: {e.Value}")));
        return (result.IsValid, errors);
    }

    public static IEnumerable<object[]> Examples() =>
        Directory.GetFiles(Path.Combine(EventsDirectory(), "examples"), "*.json", SearchOption.AllDirectories)
            .Select(f => new object[] { Path.GetRelativePath(EventsDirectory(), f) });

    [Fact]
    public void Catalog_ShouldHaveASchemaForEachOfTheFiveEvents()
    {
        Schemas.Value.Keys.Should().BeEquivalentTo(
            PaymentEvents.Created, PaymentEvents.Held, PaymentEvents.Completed, PaymentEvents.Failed, PaymentEvents.Refunded);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_ShouldMatchItsSchema(string relativePath)
    {
        var (valid, errors) = Validate(File.ReadAllText(Path.Combine(EventsDirectory(), relativePath)));

        valid.Should().BeTrue($"{relativePath} debe cumplir su esquema: {errors}");
    }

    [Theory]
    [InlineData("amount", "120750")]          // número en vez de { amount, currency }
    [InlineData("status", "\"Completed\"")]   // estado que no corresponde al tipo
    [InlineData("tarjeta", "\"4111111111111111\"")] // campo no previsto (datos sensibles)
    public void Example_Altered_ShouldBeRejected(string field, string rawValue)
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(EventsDirectory(), "examples", "flujo-billetera", "02-pago.procesado.json")))!;
        node["data"]![field] = JsonNode.Parse(rawValue);

        Validate(node.ToJsonString()).Valid.Should().BeFalse();
    }

    [Fact]
    public void Example_RefundWithoutKind_ShouldBeRejected()
    {
        var node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(EventsDirectory(), "examples", "flujo-billetera", "04-pago.reembolsado-reverso.json")))!;
        node["data"]!.AsObject().Remove("kind");

        Validate(node.ToJsonString()).Valid.Should().BeFalse();
    }

    /// <summary>Los eventos que de verdad escribe el Outbox, para cada caso, cumplen el contrato.</summary>
    [Fact]
    public async Task EventsWrittenByTheOutbox_ShouldMatchTheirSchemas()
    {
        await using var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase($"contract-{Guid.NewGuid()}").Options);
        var writer = new OutboxWriter(db);
        var rule = new CommissionRule(PaymentsDbContext.InitialCommissionRuleId, 0.15m,
            new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), null, "15 %", DateTime.UtcNow);
        var admin = new EventActor(Guid.NewGuid(), "ADMIN");

        Payment WalletPayment() => Payment.ForContract(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            rule.Quote(105000m, "COP"));

        // Pago anterior por la ruta interna (sin billetera) y su reembolso por pasarela.
        var card = new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 80000m, "COP", PaymentMethod.CreditCard);
        await writer.EnqueueAsync(PaymentEvents.PaymentCreated(card, EventActor.System));
        card.MarkAsCompleted("GW-1");
        await writer.EnqueueAsync(PaymentEvents.PaymentCompleted(card, new EventActor(card.ClientId, "CLIENT")));
        card.Refund("REF-GW-1");
        await writer.EnqueueAsync(PaymentEvents.PaymentRefunded(card, admin, "REF-GW-1", "Cobro duplicado", PaymentEvents.RefundKinds.Gateway));

        // Billetera: retener, cobrar y revertir.
        var captured = WalletPayment();
        await writer.EnqueueAsync(PaymentEvents.PaymentCreated(captured, EventActor.System));
        captured.MarkAsProcessing("HOLD-1");
        await writer.EnqueueAsync(PaymentEvents.PaymentHeld(captured, EventActor.System));
        captured.MarkAsCompleted("CAPTURE-1");
        await writer.EnqueueAsync(PaymentEvents.PaymentCompleted(captured, EventActor.System));
        captured.Refund("REF-REV-1");
        await writer.EnqueueAsync(PaymentEvents.PaymentRefunded(captured, admin, "REF-REV-1", "Disputa", PaymentEvents.RefundKinds.Reversal));

        // Billetera: retener y liberar.
        var released = WalletPayment();
        released.MarkAsProcessing("HOLD-2");
        await writer.EnqueueAsync(PaymentEvents.PaymentHeld(released, EventActor.System));
        released.ReleaseHold("REF-REL-1");
        await writer.EnqueueAsync(PaymentEvents.PaymentRefunded(released, admin, "REF-REL-1", "Objeción", PaymentEvents.RefundKinds.Release));

        // Billetera: sin saldo al contratar.
        var failed = WalletPayment();
        failed.MarkAsFailed("Saldo disponible insuficiente");
        await writer.EnqueueAsync(PaymentEvents.PaymentFailed(failed, EventActor.System));

        await db.SaveChangesAsync();
        var payloads = await db.OutboxEvents.Select(e => new { e.EventType, e.Payload }).ToListAsync();

        payloads.Should().HaveCount(10);
        payloads.Select(p => p.EventType).Distinct().Should().HaveCount(5, "se ejercitan los cinco tipos");
        foreach (var payload in payloads)
        {
            var (valid, errors) = Validate(payload.Payload);
            valid.Should().BeTrue($"{payload.EventType} debe cumplir su esquema: {errors}");
        }
    }
}
