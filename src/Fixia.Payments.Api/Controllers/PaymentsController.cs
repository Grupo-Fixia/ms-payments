using Fixia.Payments.Api.Security;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Controllers;

/// <summary>
/// API pública de pagos, detrás de Traefik. Desde GC-477 exige el token de ms-users en todas sus rutas: cada usuario
/// ve solo los pagos en los que participa (un pago ajeno responde 404, igual que uno inexistente). La creación pasó a
/// la ruta interna (<see cref="InternalPaymentsController"/>), porque la hace otro microservicio y no el usuario.
/// </summary>
[ApiController]
[Route("api/payments")]
[Authorize]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
    /// Procesa el cobro de un pago existente. Solo lo puede hacer quien figura como pagador.
    /// </summary>
    [HttpPost("{id:guid}/process")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ProcessPayment(Guid id, [FromBody] ProcessPaymentRequest? request, CancellationToken ct)
    {
        var response = await _paymentService.ProcessPaymentAsync(id, User.ToRequester(), request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Consulta el detalle de un pago por su ID. Lo ven el pagador, el prestador y un administrador de Fixia.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var response = await _paymentService.GetByIdAsync(id, User.ToRequester(), ct);
        return Ok(response);
    }

    /// <summary>
    /// Consulta el pago asociado a una solicitud de servicio específica, con las mismas reglas de visibilidad.
    /// </summary>
    [HttpGet("service-request/{serviceRequestId:guid}")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByServiceRequestId(Guid serviceRequestId, CancellationToken ct)
    {
        var response = await _paymentService.GetByServiceRequestIdAsync(serviceRequestId, User.ToRequester(), ct);
        return Ok(response);
    }

    /// <summary>
    /// Procesa el reembolso de un pago completado. Solo un administrador de Fixia.
    /// </summary>
    [HttpPost("{id:guid}/refund")]
    [Authorize(Roles = Requester.RoleAdmin)]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Refund(Guid id, [FromQuery] string? reason, CancellationToken ct)
    {
        var response = await _paymentService.RefundPaymentAsync(id, User.ToRequester(), reason, ct);
        return Ok(response);
    }
}
