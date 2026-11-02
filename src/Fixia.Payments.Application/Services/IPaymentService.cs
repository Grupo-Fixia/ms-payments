using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;

namespace Fixia.Payments.Application.Services;

public interface IPaymentService
{
    /// <summary>
    /// Registra el pago de una solicitud. Solo lo llaman otros microservicios por la ruta interna. Es idempotente:
    /// repetir la misma petición devuelve el pago existente con <c>Created = false</c>.
    /// </summary>
    Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken ct = default);
    Task<PaymentResponse> ProcessPaymentAsync(Guid paymentId, Requester requester, ProcessPaymentRequest? request = null, CancellationToken ct = default);
    Task<PaymentResponse> GetByIdAsync(Guid paymentId, Requester requester, CancellationToken ct = default);
    Task<PaymentResponse> GetByServiceRequestIdAsync(Guid serviceRequestId, Requester requester, CancellationToken ct = default);
    Task<PaymentResponse> RefundPaymentAsync(Guid paymentId, Requester requester, string? reason = null, CancellationToken ct = default);
}
