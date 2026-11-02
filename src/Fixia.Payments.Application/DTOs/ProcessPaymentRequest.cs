namespace Fixia.Payments.Application.DTOs;

public record ProcessPaymentRequest(
    string? PaymentToken = null
);

