using Fixia.Payments.Api.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Controllers;

/// <summary>
/// Rutas que solo usan otros microservicios, con <c>X-Internal-Api-Key</c> (GC-477). No pasan por Traefik.
/// </summary>
[ApiController]
[Route("internal/payments")]
[Authorize(Policy = SecuritySetup.InternalServicePolicy)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public class InternalPaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;

    public InternalPaymentsController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <summary>
    /// Registra el pago de una solicitud de servicio. Idempotente: si la misma petición ya se había registrado,
    /// responde 200 con el pago existente; si la solicitud ya tiene un pago con otros datos, responde 409.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreatePayment([FromBody] CreatePaymentRequest request, CancellationToken ct)
    {
        var result = await _paymentService.CreatePaymentAsync(request, ct);
        return result.Created
            ? Created($"/api/payments/{result.Payment.Id}", result.Payment)
            : Ok(result.Payment);
    }
}
