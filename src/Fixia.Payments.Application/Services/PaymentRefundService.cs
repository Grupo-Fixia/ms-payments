using Fixia.Payments.Application.Common;
using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Application.Services;

public interface IPaymentRefundService
{
    Task<IdempotentResult<PaymentResponse>> RefundAsync(Guid paymentId, Requester requester, string? reason,
        string? idempotencyKey, CancellationToken ct = default);
}

/// <summary>
/// Reembolso autorizado por un administrador de Fixia (GC-487). Todo es dinero virtual.
/// <list type="bullet">
/// <item><b>Pago con billetera retenido</b> (aún no cobrado): se libera el total a la billetera del cliente.</item>
/// <item><b>Pago con billetera cobrado</b>: se revierte en una sola transacción; el prestador devuelve el precio, Fixia
/// devuelve la comisión y el cliente recibe el total. Si el prestador ya retiró y no le alcanza, responde 409 sin
/// cambiar nada.</item>
/// <item><b>Pago anterior sin billetera</b>: se reembolsa por la pasarela, como antes.</item>
/// </list>
/// Exige motivo e <c>Idempotency-Key</c>: repetirlo no devuelve dos veces. Como un servicio contratado no se puede
/// cancelar (decisión del 2026-11-02), no hay liberación automática: solo esta, por un administrador (por ejemplo al
/// resolver una objeción del cierre).
/// </summary>
public class PaymentRefundService : IPaymentRefundService
{
    public const string Operation = "payment.refund";
    public const int MaxReasonLength = 500;

    private readonly IPaymentRepository _payments;
    private readonly IWalletRepository _wallets;
    private readonly IPaymentGateway _gateway;
    private readonly IBusinessEventOutbox _outbox;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaymentRefundService> _logger;

    public PaymentRefundService(IPaymentRepository payments, IWalletRepository wallets, IPaymentGateway gateway,
        IBusinessEventOutbox outbox, IIdempotencyStore idempotency, IUnitOfWork unitOfWork, TimeProvider clock,
        ILogger<PaymentRefundService> logger)
    {
        _payments = payments;
        _wallets = wallets;
        _gateway = gateway;
        _outbox = outbox;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IdempotentResult<PaymentResponse>> RefundAsync(Guid paymentId, Requester requester,
        string? reason, string? idempotencyKey, CancellationToken ct = default)
    {
        if (!requester.IsAdmin)
            throw new PaymentAccessDeniedException("Solo un administrador de Fixia puede reembolsar un pago.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidQueryException("El motivo del reembolso (reason) es obligatorio.");
        var cleanReason = reason.Trim();
        if (cleanReason.Length > MaxReasonLength)
            throw new InvalidQueryException($"El motivo admite como máximo {MaxReasonLength} caracteres.");
        var key = IdempotencyKeys.Validate(idempotencyKey);

        return await _idempotency.RunAsync(requester.UserId, Operation, key,
            IdempotencyKeys.Hash($"{paymentId:N}|{cleanReason}"),
            async token =>
            {
                var payment = await _payments.GetByIdAsync(paymentId, token)
                              ?? throw new PaymentNotFoundException(paymentId);
                var actor = new EventActor(requester.UserId, requester.ActorRole);

                var (reference, kind) = payment.IsWalletPayment
                    ? await RefundWalletPaymentAsync(payment, token)
                    : await RefundThroughGatewayAsync(payment, token);

                _payments.Update(payment);
                await _outbox.EnqueueAsync(PaymentEvents.PaymentRefunded(payment, actor, reference, cleanReason, kind), token);
                await _unitOfWork.SaveChangesAsync(token);

                _logger.LogInformation("Pago '{PaymentId}' reembolsado ({Kind}).", payment.Id, kind);
                return payment.ToResponse(requester);
            }, ct);
    }

    private async Task<(string Reference, string Kind)> RefundWalletPaymentAsync(Payment payment, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var reference = $"REF-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        var client = await RequireWalletAsync(payment.ClientId, payment.Currency, "del cliente", ct);

        switch (payment.Status)
        {
            case PaymentStatus.Processing:
                // Retenido y sin cobrar: vuelve al disponible del cliente.
                await _wallets.AddEntryAsync(client.Apply(WalletEntryType.Release, payment.Amount, now, payment.Id, reference), ct);
                payment.ReleaseHold(reference);
                return (reference, PaymentEvents.RefundKinds.Release);

            case PaymentStatus.Completed:
                // Ya cobrado: prestador y Fixia devuelven lo que recibieron; si alguno no tiene saldo, Apply lanza 409
                // y la transacción completa se revierte.
                var provider = await RequireWalletAsync(payment.ProviderId, payment.Currency, "del prestador", ct);
                await _wallets.AddEntryAsync(provider.Apply(WalletEntryType.Reversal, payment.BaseAmount!.Value, now,
                    payment.Id, reference), ct);
                if (payment.CommissionAmount is > 0m)
                {
                    var platform = await RequireWalletAsync(Wallet.PlatformOwnerId, payment.Currency, "de Fixia", ct);
                    await _wallets.AddEntryAsync(platform.Apply(WalletEntryType.Reversal, payment.CommissionAmount.Value,
                        now, payment.Id, reference), ct);
                }

                await _wallets.AddEntryAsync(client.Apply(WalletEntryType.Refund, payment.Amount, now, payment.Id, reference), ct);
                payment.Refund(reference);
                return (reference, PaymentEvents.RefundKinds.Reversal);

            default:
                throw new InvalidPaymentStatusException(payment.Status, "Reembolsar pago con billetera");
        }
    }

    private async Task<(string Reference, string Kind)> RefundThroughGatewayAsync(Payment payment, CancellationToken ct)
    {
        var gatewayResult = await _gateway.ProcessRefundAsync(
            payment.Id, payment.TransactionReference ?? payment.Id.ToString(), payment.Amount, ct);
        if (!gatewayResult.IsSuccess)
            throw new GatewayRejectedException($"La pasarela de pago denegó el reembolso: {gatewayResult.ErrorMessage}");

        var reference = $"REF-{Guid.NewGuid():N}";
        payment.Refund(reference, gatewayResult.RawResponse);
        return (reference, PaymentEvents.RefundKinds.Gateway);
    }

    private async Task<Wallet> RequireWalletAsync(Guid ownerId, string currency, string whose, CancellationToken ct) =>
        await _wallets.GetByOwnerAsync(ownerId, currency, ct)
        ?? throw new PaymentConflictException($"No existe la billetera {whose}; el pago no se puede reembolsar.");
}
