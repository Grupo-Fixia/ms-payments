using System.Security.Claims;
using Fixia.Payments.Application.Common.Security;

namespace Fixia.Payments.Api.Security;

/// <summary>Convierte el usuario autenticado por el token en un <see cref="Requester"/> (GC-477).</summary>
public static class RequesterExtensions
{
    /// <summary>El claim <c>sub</c> de los tokens de ms-users tiene la forma <c>usr_&lt;uuid&gt;</c>.</summary>
    public const string SubjectPrefix = "usr_";
    public const string SubjectClaim = "sub";
    public const string RolesClaim = "roles";

    public static bool TryParseSubject(string? subject, out Guid userId)
    {
        userId = Guid.Empty;
        return subject is not null
               && subject.StartsWith(SubjectPrefix, StringComparison.Ordinal)
               && Guid.TryParse(subject[SubjectPrefix.Length..], out userId);
    }

    /// <summary>
    /// Solo se llama en acciones con <c>[Authorize]</c>: el token ya se validó, incluido el formato del <c>sub</c>.
    /// </summary>
    public static Requester ToRequester(this ClaimsPrincipal user)
    {
        if (!TryParseSubject(user.FindFirstValue(SubjectClaim), out var userId))
            throw new InvalidOperationException("El token autenticado no trae un sub válido");

        var roles = user.FindAll(RolesClaim).Select(c => c.Value).ToArray();
        return new Requester(userId, roles);
    }
}
