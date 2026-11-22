using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Application.Services;

/// <summary>
/// Pregunta de ms-users antes de desvincular a un técnico de su empresa (GC-524, contrato de empresas §6.2): el
/// prestador y las solicitudes en que ese técnico fue el designado.
/// </summary>
public sealed record HeldPaymentsRequest(Guid ProviderId, IReadOnlyList<Guid>? ServiceRequestIds);

/// <summary>Las solicitudes de la lista con dinero del prestador todavía sin resolver.</summary>
public sealed record HeldPaymentsResponse(IReadOnlyList<Guid> HeldServiceRequestIds);

public interface IHeldPaymentsService
{
    Task<HeldPaymentsResponse> HeldAsync(HeldPaymentsRequest request, CancellationToken ct = default);
}

/// <summary>
/// Dinero retenido de un prestador sobre unas solicitudes (GC-524). Cuenta como retenido un pago por billetera
/// registrado y aún sin retener (<c>Pending</c>) o con la retención hecha y sin capturar ni liberar (<c>Held</c>): el
/// mismo criterio que <c>HoldStatusOf</c>. Un pago puede seguir retenido aunque el servicio ya esté cerrado, por
/// ejemplo si la captura falló; por eso ms-users no se fía solo del estado del servicio.
/// </summary>
public sealed class HeldPaymentsService : IHeldPaymentsService
{
    /// <summary>Máximo de solicitudes por consulta, para acotar la sentencia.</summary>
    public const int MaxServiceRequestIds = 500;

    private readonly IPaymentRepository _payments;

    public HeldPaymentsService(IPaymentRepository payments)
    {
        _payments = payments;
    }

    public async Task<HeldPaymentsResponse> HeldAsync(HeldPaymentsRequest request, CancellationToken ct = default)
    {
        if (request.ProviderId == Guid.Empty)
            throw new InvalidQueryException("providerId es obligatorio");
        var ids = request.ServiceRequestIds ?? Array.Empty<Guid>();
        if (ids.Count > MaxServiceRequestIds)
            throw new InvalidQueryException($"Se admiten hasta {MaxServiceRequestIds} solicitudes por consulta");
        if (ids.Any(id => id == Guid.Empty))
            throw new InvalidQueryException("serviceRequestIds no admite identificadores vacíos");
        if (ids.Count == 0)
            return new HeldPaymentsResponse(Array.Empty<Guid>());

        var held = await _payments.ListHeldServiceRequestIdsAsync(request.ProviderId, ids.Distinct().ToList(), ct);
        return new HeldPaymentsResponse(held);
    }
}
