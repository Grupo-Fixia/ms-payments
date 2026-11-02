using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Infrastructure.Gateways;

public class MockPaymentGateway : IPaymentGateway
{
    private readonly ILogger<MockPaymentGateway> _logger;

    public MockPaymentGateway(ILogger<MockPaymentGateway> logger)
    {
        _logger = logger;
    }

    public Task<PaymentGatewayResult> ProcessChargeAsync(
        Guid paymentId,
        decimal amount,
        string currency,
        PaymentMethod method,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Simulando cobro en pasarela para PaymentId: {PaymentId}, Monto: {Amount} {Currency}, Método: {Method}",
            paymentId, amount, currency, method);

        // Simulamos respuesta aprobada
        var txReference = $"GW-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        var rawResponse = $"{{\"status\":\"APPROVED\",\"gateway\":\"FixiaMockGateway\",\"authorizationCode\":\"{Guid.NewGuid():N}\"[..8]}}";

        return Task.FromResult(new PaymentGatewayResult(
            IsSuccess: true,
            TransactionReference: txReference,
            RawResponse: rawResponse
        ));
    }

    public Task<PaymentGatewayResult> ProcessRefundAsync(
        Guid paymentId,
        string transactionReference,
        decimal amount,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Simulando reembolso en pasarela para PaymentId: {PaymentId}, Ref: {Ref}, Monto: {Amount}",
            paymentId, transactionReference, amount);

        var refundRef = $"REF-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        var rawResponse = $"{{\"status\":\"REFUNDED\",\"originalReference\":\"{transactionReference}\",\"refundId\":\"{refundRef}\"}}";

        return Task.FromResult(new PaymentGatewayResult(
            IsSuccess: true,
            TransactionReference: refundRef,
            RawResponse: rawResponse
        ));
    }
}

