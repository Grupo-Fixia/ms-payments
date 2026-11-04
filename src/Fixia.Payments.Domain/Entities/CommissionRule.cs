using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Domain.Entities;

/// <summary>
/// Tarifa de comisión de Fixia con vigencia (GC-480). La paga el cliente encima del precio y el prestador recibe el
/// precio completo (GC-476). No es una constante del código: vive en la tabla <c>commission_rules</c>; para cambiarla se
/// cierra la vigencia de la actual y se agrega otra, así cada pago conserva la regla con la que se calculó.
/// </summary>
public class CommissionRule
{
    public Guid Id { get; private set; }

    /// <summary>Fracción del precio: 0,15 es 15 %.</summary>
    public decimal Rate { get; private set; }

    public DateTime ValidFrom { get; private set; }
    public DateTime? ValidTo { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }

    // Constructor para EF Core
    private CommissionRule() { }

    public CommissionRule(Guid id, decimal rate, DateTime validFrom, DateTime? validTo, string description,
        DateTime createdAt)
    {
        if (rate <= 0 || rate >= 1)
            throw new PaymentDomainException("La tarifa de comisión debe estar entre 0 y 1 (sin incluirlos).");
        if (validTo.HasValue && validTo.Value <= validFrom)
            throw new PaymentDomainException("El fin de la vigencia debe ser posterior a su inicio.");

        Id = id;
        Rate = rate;
        ValidFrom = validFrom;
        ValidTo = validTo;
        Description = description;
        CreatedAt = createdAt;
    }

    public bool IsValidAt(DateTime instant) => ValidFrom <= instant && (ValidTo is null || instant < ValidTo);

    /// <summary>Precio del prestador, comisión de Fixia (redondeada a dos decimales) y total que paga el cliente.</summary>
    public CommissionQuote Quote(decimal price, string currency)
    {
        Money.RequirePositive(price);
        var commission = Money.Round(price * Rate);
        return new CommissionQuote(price, commission, price + commission,
            string.IsNullOrWhiteSpace(currency) ? Wallet.DefaultCurrency : currency.Trim().ToUpperInvariant(),
            Rate, Id);
    }
}

/// <summary>Desglose de un pago: el cliente paga <c>Total = Price + Commission</c>.</summary>
public sealed record CommissionQuote(decimal Price, decimal Commission, decimal Total, string Currency, decimal Rate,
    Guid RuleId);
