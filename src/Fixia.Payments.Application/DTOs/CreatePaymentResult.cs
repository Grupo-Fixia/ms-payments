namespace Fixia.Payments.Application.DTOs;

/// <summary>
/// Resultado de registrar un pago: <c>Created</c> es false cuando la misma petición ya se había registrado antes
/// (reintento), y entonces la API responde 200 en vez de 201.
/// </summary>
public record CreatePaymentResult(PaymentResponse Payment, bool Created);
