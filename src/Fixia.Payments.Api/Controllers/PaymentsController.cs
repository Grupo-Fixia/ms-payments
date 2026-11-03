using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Controllers;

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(IPaymentService paymentService, ILogger<PaymentsController> logger)
    {
        _paymentService = paymentService;
        _logger = logger;
    }

    /// <summary>
    /// Crea una nueva intención o registro de pago
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePayment([FromBody] CreatePaymentRequest request, CancellationToken ct)
    {
        var response = await _paymentService.CreatePaymentAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = response.Id }, response);
    }

    /// <summary>
    /// Procesa el cobro de un pago existente
    /// </summary>
    [HttpPost("{id:guid}/process")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ProcessPayment(Guid id, [FromBody] ProcessPaymentRequest? request, CancellationToken ct)
    {
        var response = await _paymentService.ProcessPaymentAsync(id, request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Consulta el detalle de un pago por su ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var response = await _paymentService.GetByIdAsync(id, ct);
        return Ok(response);
    }

    /// <summary>
    /// Consulta el pago asociado a una solicitud de servicio específica
    /// </summary>
    [HttpGet("service-request/{serviceRequestId:guid}")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByServiceRequestId(Guid serviceRequestId, CancellationToken ct)
    {
        var response = await _paymentService.GetByServiceRequestIdAsync(serviceRequestId, ct);
        return Ok(response);
    }

    /// <summary>
    /// Procesa el reembolso de un pago completado
    /// </summary>
    [HttpPost("{id:guid}/refund")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Refund(Guid id, [FromQuery] string? reason, CancellationToken ct)
    {
        var response = await _paymentService.RefundPaymentAsync(id, reason, ct);
        return Ok(response);
    }
}

