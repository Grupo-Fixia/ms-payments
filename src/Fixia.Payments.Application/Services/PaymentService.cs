using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Application.Services;

/// <summary>
/// Casos de uso de pagos. Desde GC-477 los eventos se escriben en el Outbox dentro del mismo <c>SaveChangesAsync</c>
/// que el cambio (nunca antes de confirmarlo) y cada operación comprueba quién la pide.
/// </summary>
public class PaymentService : IPaymentService
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPaymentGateway _paymentGateway;
    private readonly IBusinessEventOutbox _outbox;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        IPaymentRepository paymentRepository,
        IUnitOfWork unitOfWork,
        IPaymentGateway paymentGateway,
        IBusinessEventOutbox outbox,
        ILogger<PaymentService> logger)
    {
        _paymentRepository = paymentRepository;
        _unitOfWork = unitOfWork;
        _paymentGateway = paymentGateway;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("Creando intención de pago para ServiceRequest '{ServiceRequestId}'.", request.ServiceRequestId);

        // Un solo pago por solicitud (índice único). Repetir la misma petición es un reintento y devuelve el existente.
        var existing = await _paymentRepository.GetByServiceRequestIdAsync(request.ServiceRequestId, ct);
        if (existing != null)
        {
            if (IsSameRequest(existing, request))
            {
                _logger.LogInformation("Reintento de creación: el pago '{PaymentId}' ya existía.", existing.Id);
                return new CreatePaymentResult(existing.ToResponse(), Created: false);
            }

            throw new PaymentConflictException(
                $"La solicitud de servicio '{request.ServiceRequestId}' ya tiene un pago registrado con otros datos.");
        }

        var payment = new Payment(
            request.ServiceRequestId,
            request.ClientId,
            request.ProviderId,
            request.Amount,
            request.Currency,
            request.Method
        );

        await _paymentRepository.AddAsync(payment, ct);
        await _outbox.EnqueueAsync(PaymentEvents.PaymentCreated(payment, EventActor.System), ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("Pago registrado exitosamente con ID '{PaymentId}'.", payment.Id);
        return new CreatePaymentResult(payment.ToResponse(), Created: true);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(Guid paymentId, Requester requester,
        ProcessPaymentRequest? request = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Iniciando procesamiento de cobro para el pago '{PaymentId}'.", paymentId);

        var payment = await GetVisiblePaymentAsync(paymentId, requester, ct);
        if (!requester.IsPayerOf(payment))
            throw new PaymentAccessDeniedException("Solo quien paga puede procesar el cobro de este pago.");

        var preRef = $"TX-{Guid.NewGuid():N}";
        payment.MarkAsProcessing(preRef);
        _paymentRepository.Update(payment);
        await _unitOfWork.SaveChangesAsync(ct);

        var gatewayResult = await _paymentGateway.ProcessChargeAsync(
            payment.Id,
            payment.Amount,
            payment.Currency,
            payment.Method,
            ct);

        var actor = new EventActor(requester.UserId, requester.ActorRole);
        if (gatewayResult.IsSuccess)
        {
            payment.MarkAsCompleted(gatewayResult.TransactionReference, gatewayResult.RawResponse);
            await _outbox.EnqueueAsync(PaymentEvents.PaymentCompleted(payment, actor), ct);
            _logger.LogInformation("Pago '{PaymentId}' completado exitosamente con referencia '{Reference}'.",
                payment.Id, gatewayResult.TransactionReference);
        }
        else
        {
            var failureMsg = gatewayResult.ErrorMessage ?? "Error desconocido en la pasarela de pagos.";
            payment.MarkAsFailed(failureMsg, gatewayResult.RawResponse);
            await _outbox.EnqueueAsync(PaymentEvents.PaymentFailed(payment, actor), ct);
            _logger.LogWarning("Pago '{PaymentId}' rechazado o fallido: {Reason}", payment.Id, failureMsg);
        }

        _paymentRepository.Update(payment);
        await _unitOfWork.SaveChangesAsync(ct);

        return payment.ToResponse();
    }

    public async Task<PaymentResponse> GetByIdAsync(Guid paymentId, Requester requester, CancellationToken ct = default)
    {
        var payment = await GetVisiblePaymentAsync(paymentId, requester, ct);
        return payment.ToResponse();
    }

    public async Task<PaymentResponse> GetByServiceRequestIdAsync(Guid serviceRequestId, Requester requester,
        CancellationToken ct = default)
    {
        var payment = await _paymentRepository.GetByServiceRequestIdAsync(serviceRequestId, ct);
        if (payment == null || !requester.CanView(payment))
            throw PaymentNotFoundException.ForServiceRequest(serviceRequestId);

        return payment.ToResponse();
    }

    public async Task<PaymentResponse> RefundPaymentAsync(Guid paymentId, Requester requester, string? reason = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Iniciando solicitud de reembolso para el pago '{PaymentId}'.", paymentId);

        if (!requester.IsAdmin)
            throw new PaymentAccessDeniedException("Solo un administrador de Fixia puede reembolsar un pago.");

        var payment = await _paymentRepository.GetByIdAsync(paymentId, ct);
        if (payment == null)
            throw new PaymentNotFoundException(paymentId);

        var refundRef = $"REF-{Guid.NewGuid():N}";
        var gatewayResult = await _paymentGateway.ProcessRefundAsync(
            payment.Id,
            payment.TransactionReference ?? payment.Id.ToString(),
            payment.Amount,
            ct);

        if (!gatewayResult.IsSuccess)
        {
            throw new PaymentDomainException($"La pasarela de pago denegó el reembolso: {gatewayResult.ErrorMessage}");
        }

        payment.Refund(refundRef, gatewayResult.RawResponse);
        _paymentRepository.Update(payment);
        await _outbox.EnqueueAsync(PaymentEvents.PaymentRefunded(payment,
            new EventActor(requester.UserId, requester.ActorRole), refundRef, reason), ct);
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("Reembolso del pago '{PaymentId}' completado exitosamente.", payment.Id);
        return payment.ToResponse();
    }

    /// <summary>Un pago ajeno responde igual que uno inexistente (404), para no revelar que existe.</summary>
    private async Task<Payment> GetVisiblePaymentAsync(Guid paymentId, Requester requester, CancellationToken ct)
    {
        var payment = await _paymentRepository.GetByIdAsync(paymentId, ct);
        if (payment == null || !requester.CanView(payment))
            throw new PaymentNotFoundException(paymentId);

        return payment;
    }

    private static bool IsSameRequest(Payment existing, CreatePaymentRequest request) =>
        existing.ClientId == request.ClientId
        && existing.ProviderId == request.ProviderId
        && existing.Amount == request.Amount
        && string.Equals(existing.Currency, NormalizeCurrency(request.Currency), StringComparison.Ordinal)
        && existing.Method == request.Method;

    private static string NormalizeCurrency(string currency) =>
        string.IsNullOrWhiteSpace(currency) ? "COP" : currency.ToUpperInvariant();
}
