using Fixia.Payments.Domain.Entities;

namespace Fixia.Payments.Application.Common.Security;

/// <summary>
/// Quién hace la petición, tomado del token de ms-users (GC-477): <c>UserId</c> es el <c>sub</c> sin el prefijo
/// <c>usr_</c> (el <c>users.id</c>) y <c>Roles</c> son los del claim <c>roles</c>, con prefijo (<c>ROLE_CLIENT</c>).
/// </summary>
public sealed record Requester(Guid UserId, IReadOnlyCollection<string> Roles)
{
    public const string RoleAdmin = "ROLE_ADMIN";
    public const string RoleClient = "ROLE_CLIENT";
    public const string RoleProfessional = "ROLE_PROFESSIONAL";
    public const string RoleCompany = "ROLE_COMPANY";

    private static readonly string[] ActorRolesByPriority = { RoleAdmin, RoleClient, RoleProfessional, RoleCompany };

    public bool IsAdmin => Roles.Contains(RoleAdmin);

    /// <summary>Puede ver un pago quien lo paga, quien lo recibe o un administrador de Fixia.</summary>
    public bool CanView(Payment payment) =>
        IsAdmin || payment.ClientId == UserId || payment.ProviderId == UserId;

    /// <summary>Solo quien figura como pagador puede procesar el cobro.</summary>
    public bool IsPayerOf(Payment payment) => payment.ClientId == UserId;

    /// <summary>Rol con el que actúa, en el formato del sobre de eventos (sin el prefijo <c>ROLE_</c>).</summary>
    public string ActorRole
    {
        get
        {
            var role = ActorRolesByPriority.FirstOrDefault(Roles.Contains);
            return role is null ? "UNKNOWN" : role["ROLE_".Length..];
        }
    }
}
