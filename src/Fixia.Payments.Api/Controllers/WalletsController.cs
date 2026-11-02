using Fixia.Payments.Api.Security;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Controllers;

/// <summary>
/// Billetera simulada propia (GC-481). No recibe ningún id: la billetera es siempre la del usuario del token, así que
/// no se puede pedir la de otro. Solo clientes, técnicos y empresas tienen billetera (el resto recibe 403).
/// </summary>
[ApiController]
[Route("api/payments/wallets")]
[Authorize(Roles = Requester.WalletRoles)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class WalletsController : ControllerBase
{
    private readonly IWalletService _wallets;

    public WalletsController(IWalletService wallets)
    {
        _wallets = wallets;
    }

    /// <summary>
    /// Saldo disponible y retenido de mi billetera. Si todavía no existe, se crea en cero.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(WalletResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMine(CancellationToken ct) =>
        Ok(await _wallets.GetMyWalletAsync(User.ToRequester(), ct));

    /// <summary>
    /// Movimientos de mi billetera, del más reciente al más antiguo. <c>limit</c> entre 1 y 50 (20 por defecto);
    /// <c>cursor</c> es el <c>nextCursor</c> de la página anterior.
    /// </summary>
    [HttpGet("me/entries")]
    [ProducesResponseType(typeof(WalletEntriesPage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMyEntries([FromQuery] string? limit, [FromQuery] string? cursor,
        CancellationToken ct)
    {
        // El límite se recibe como texto para responder 400 con un mensaje claro si no es un número.
        int? size = null;
        if (!string.IsNullOrWhiteSpace(limit))
        {
            if (!int.TryParse(limit.Trim(), out var parsed))
                throw new Domain.Exceptions.InvalidQueryException(
                    $"El límite debe ser un número entre 1 y {WalletService.MaxLimit}.");
            size = parsed;
        }

        return Ok(await _wallets.GetMyEntriesAsync(User.ToRequester(), size, cursor, ct));
    }
}
