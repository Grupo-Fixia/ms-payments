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
    public async Task<IActionResult> GetById(Guid id, [FromServices] IAdminPaymentsService admin, CancellationToken ct)
    {
        var requester = User.ToRequester();
        var response = await AuditedForAdminAsync(requester, admin, id, null,
            () => _paymentService.GetByIdAsync(id, requester, ct), ct);
        return Ok(response);
    }

    /// <summary>
    /// Consulta el pago asociado a una solicitud de servicio específica, con las mismas reglas de visibilidad.
    /// </summary>
    [HttpGet("service-request/{serviceRequestId:guid}")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByServiceRequestId(Guid serviceRequestId,
        [FromServices] IAdminPaymentsService admin, CancellationToken ct)
    {
        var requester = User.ToRequester();
        var response = await AuditedForAdminAsync(requester, admin, null, new { serviceRequestId },
            () => _paymentService.GetByServiceRequestIdAsync(serviceRequestId, requester, ct), ct);
        return Ok(response);
    }

    /// <summary>
    /// Si quien consulta es un ADMIN, la consulta queda en su registro de acciones (GC-535, contrato admin §4.2), también
    /// cuando el pago no existe. Para cliente, técnico o empresa no se registra nada.
    /// </summary>
    private static async Task<PaymentResponse> AuditedForAdminAsync(Requester requester, IAdminPaymentsService admin,
        Guid? paymentId, object? details, Func<Task<PaymentResponse>> query, CancellationToken ct)
    {
        if (!requester.IsAdmin)
            return await query();

        try
        {
            var response = await query();
            await admin.RecordViewAsync(requester, response.Id, found: true, details, ct);
            return response;
        }
        catch (Domain.Exceptions.PaymentNotFoundException)
        {
            await admin.RecordViewAsync(requester, paymentId, found: false, details, ct);
            throw;
        }
    }

    /// <summary>
    /// Reembolso autorizado (GC-487), solo un administrador de Fixia. Con billetera: si el pago está retenido libera el
    /// total al cliente; si ya se cobró, lo revierte (prestador y Fixia devuelven, el cliente recibe) o responde 409 si
    /// no se puede sin dejar saldos negativos. Exige <c>reason</c> e <c>Idempotency-Key</c>: repetirlo no devuelve dos
    /// veces (responde lo mismo con <c>Idempotent-Replayed: true</c>).
    /// </summary>
    [HttpPost("{id:guid}/refund")]
    [Authorize(Roles = Requester.RoleAdmin)]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Refund(Guid id, [FromQuery] string? reason,
        [FromHeader(Name = WalletsController.IdempotencyKeyHeader)] string? idempotencyKey,
        [FromServices] IPaymentRefundService refunds, CancellationToken ct)
    {
        var result = await refunds.RefundAsync(id, User.ToRequester(), reason, idempotencyKey, ct);
        if (result.Replayed)
            Response.Headers[WalletsController.ReplayedHeader] = "true";
        return Ok(result.Value);
    }
}
