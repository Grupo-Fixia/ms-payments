using Fixia.Payments.Application.DTOs;

namespace Fixia.Payments.Application.Services;

public interface IPaymentService
{
    Task<PaymentResponse> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken ct = default);
    Task<PaymentResponse> ProcessPaymentAsync(Guid paymentId, ProcessPaymentRequest? request = null, CancellationToken ct = default);
    Task<PaymentResponse> GetByIdAsync(Guid paymentId, CancellationToken ct = default);
    Task<PaymentResponse> GetByServiceRequestIdAsync(Guid serviceRequestId, CancellationToken ct = default);
    Task<PaymentResponse> RefundPaymentAsync(Guid paymentId, string? reason = null, CancellationToken ct = default);
}

