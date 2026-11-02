using Fixia.Payments.Domain.Enums;

namespace Fixia.Payments.Application.DTOs;

public record CreatePaymentRequest(
    Guid ServiceRequestId,
    Guid ClientId,
    Guid ProviderId,
    decimal Amount,
    string Currency,
    PaymentMethod Method
);

