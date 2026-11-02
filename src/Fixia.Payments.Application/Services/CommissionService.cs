using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Application.Services;

public interface ICommissionService
{
    /// <summary>La regla vigente ahora y el desglose de un precio con ella.</summary>
    Task<(CommissionRule Rule, CommissionQuote Quote)> QuoteNowAsync(decimal price, string? currency,
        CancellationToken ct = default);
}

/// <summary>
/// Cálculo de la comisión con la tarifa vigente (GC-480). Lo usan la cotización que ve el cliente antes de aceptar
/// (GC-471, criterio 1) y, después, la retención al contratar (GC-484), para que ambos den exactamente lo mismo.
/// </summary>
public class CommissionService : ICommissionService
{
    private readonly ICommissionRuleRepository _rules;
    private readonly TimeProvider _clock;

    public CommissionService(ICommissionRuleRepository rules, TimeProvider clock)
    {
        _rules = rules;
        _clock = clock;
    }

    public async Task<(CommissionRule Rule, CommissionQuote Quote)> QuoteNowAsync(decimal price, string? currency,
        CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(currency) ? Wallet.DefaultCurrency : currency.Trim().ToUpperInvariant();
        if (normalized != Wallet.DefaultCurrency)
            throw new InvalidQueryException($"Por ahora solo se maneja {Wallet.DefaultCurrency}.");
        if (price <= 0 || decimal.Round(price, Money.Decimals) != price)
            throw new InvalidQueryException("El precio debe ser mayor que cero y tener como máximo dos decimales.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var rule = await _rules.GetValidAtAsync(now, ct) ?? throw new CommissionRuleNotFoundException(now);
        return (rule, rule.Quote(price, normalized));
    }
}
