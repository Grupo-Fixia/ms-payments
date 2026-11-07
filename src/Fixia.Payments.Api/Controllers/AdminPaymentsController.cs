using Fixia.Payments.Api.Security;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Controllers;

/// <summary>
/// Consultas del ADMIN de Fixia sobre pagos (GC-535, contrato <c>ms-users/docs/contracts/admin.md</c> §4.1 y §4.4).
/// Solo <c>ROLE_ADMIN</c>: sin token 401, otro rol 403. El reembolso sigue en <see cref="PaymentsController"/>
/// (GC-487) y las consultas de un pago también; aquí están la lista y el registro de acciones.
/// </summary>
[ApiController]
[Route("api/payments/admin")]
[Authorize(Roles = Requester.RoleAdmin)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class AdminPaymentsController : ControllerBase
{
    private readonly IAdminPaymentsService _admin;

    public AdminPaymentsController(IAdminPaymentsService admin)
    {
        _admin = admin;
    }

    /// <summary>
    /// Pagos para soporte, del más reciente al más antiguo, con <c>refundable</c> y <c>refundKind</c>. Filtros
    /// opcionales: <c>status</c>, <c>holdStatus</c>, <c>serviceRequestId</c> y <c>userId</c> (cliente o prestador).
    /// </summary>
    [HttpGet("payments")]
    [ProducesResponseType(typeof(AdminPaymentPage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListPayments([FromQuery] string? status, [FromQuery] string? holdStatus,
        [FromQuery] Guid? serviceRequestId, [FromQuery] Guid? userId, [FromQuery] int? limit,
        [FromQuery] string? cursor, CancellationToken ct)
    {
        var page = await _admin.ListPaymentsAsync(User.ToRequester(), status, holdStatus, serviceRequestId, userId,
            limit, cursor, ct);
        return Ok(page);
    }

    /// <summary>Registro de acciones del ADMIN en ms-payments, del más reciente al más antiguo.</summary>
    [HttpGet("audit-events")]
    [ProducesResponseType(typeof(AdminAuditPage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListAuditEvents([FromQuery] Guid? adminId, [FromQuery] string? action,
        [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
    {
        var page = await _admin.ListAuditEventsAsync(User.ToRequester(), adminId, action, limit, cursor, ct);
        return Ok(page);
    }
}
