using System.Globalization;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Controllers;

/// <summary>
/// Desglose del pago antes de aceptar una cotización (GC-471, criterio 1; lo consume el Flutter de GC-491): precio del
/// prestador, comisión de Fixia a cargo del cliente y total, con la tarifa vigente. No guarda nada.
/// </summary>
[ApiController]
[Route("api/payments/commission-quote")]
[Authorize]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public class CommissionController : ControllerBase
{
    private readonly ICommissionService _commission;

    public CommissionController(ICommissionService commission)
    {
        _commission = commission;
    }

    /// <summary>
    /// <c>amount</c> es el precio del prestador (por ejemplo el de la cotización), con punto decimal y como máximo dos
    /// decimales; <c>currency</c> es opcional (COP).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(CommissionQuoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Quote([FromQuery] string? amount, [FromQuery] string? currency,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(amount)
            || !decimal.TryParse(amount.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price))
            throw new InvalidQueryException("El precio (amount) es obligatorio y debe ser un número, por ejemplo 105000.00.");

        var (rule, quote) = await _commission.QuoteNowAsync(price, currency, ct);
        return Ok(new CommissionQuoteResponse(quote.Price, quote.Commission, quote.Total, quote.Currency, quote.Rate,
            quote.RuleId, rule.ValidFrom));
    }
}
