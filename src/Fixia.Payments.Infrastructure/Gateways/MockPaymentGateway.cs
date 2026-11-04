using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Infrastructure.Gateways;

public class MockPaymentGateway : IPaymentGateway
{
    /// <summary>
    /// Recargas y retiros por encima de este monto se rechazan, para poder demostrar y probar el rechazo (GC-482,
    /// GC-483). Se cambia con <c>PaymentGateway__Mock__RejectAmountsAbove</c>.
    /// </summary>
    public const decimal DefaultRejectAmountsAbove = 10_000_000m;

    private readonly ILogger<MockPaymentGateway> _logger;
    private readonly decimal _rejectAmountsAbove;

    public MockPaymentGateway(ILogger<MockPaymentGateway> logger, IConfiguration? configuration = null)
    {
        _logger = logger;
        _rejectAmountsAbove = configuration?.GetValue<decimal?>("PaymentGateway:Mock:RejectAmountsAbove")
                              ?? DefaultRejectAmountsAbove;
    }

    public Task<PaymentGatewayResult> ProcessTopUpAsync(Guid walletId, decimal amount, string currency,
        CancellationToken ct = default) =>
        Task.FromResult(Simulate("TOPUP", walletId, amount, currency));

    public Task<PaymentGatewayResult> ProcessPayoutAsync(Guid walletId, decimal amount, string currency,
        CancellationToken ct = default) =>
        Task.FromResult(Simulate("PAYOUT", walletId, amount, currency));

    private PaymentGatewayResult Simulate(string operation, Guid walletId, decimal amount, string currency)
    {
        var reference = $"{operation}-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..32].ToUpperInvariant();
        if (amount > _rejectAmountsAbove)
        {
            _logger.LogInformation("Pasarela simulada: {Operation} rechazado para la billetera {WalletId} ({Amount} {Currency}).",
                operation, walletId, amount, currency);
            return new PaymentGatewayResult(false, reference,
                $"La pasarela simulada rechazó la operación: supera el máximo de {_rejectAmountsAbove:0.00} {currency}.",
                "{\"status\":\"REJECTED\",\"gateway\":\"FixiaMockGateway\"}");
        }

        _logger.LogInformation("Pasarela simulada: {Operation} aprobado para la billetera {WalletId} ({Amount} {Currency}).",
            operation, walletId, amount, currency);
        return new PaymentGatewayResult(true, reference, null,
            "{\"status\":\"APPROVED\",\"gateway\":\"FixiaMockGateway\"}");
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

