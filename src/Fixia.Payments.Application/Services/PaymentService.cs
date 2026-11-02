using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Application.Services;

public class PaymentService : IPaymentService
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPaymentGateway _paymentGateway;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        IPaymentRepository paymentRepository,
        IUnitOfWork unitOfWork,
        IPaymentGateway paymentGateway,
        IEventPublisher eventPublisher,
        ILogger<PaymentService> logger)
    {
        _paymentRepository = paymentRepository;
        _unitOfWork = unitOfWork;
        _paymentGateway = paymentGateway;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<PaymentResponse> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("Creando intención de pago para ServiceRequest '{ServiceRequestId}' y Cliente '{ClientId}'.",
            request.ServiceRequestId, request.ClientId);

        // Verificar si ya existe un pago previo para esta solicitud de servicio
        var existing = await _paymentRepository.GetByServiceRequestIdAsync(request.ServiceRequestId, ct);
        if (existing != null && existing.Status == Domain.Enums.PaymentStatus.Completed)
        {
            throw new PaymentDomainException($"La solicitud de servicio '{request.ServiceRequestId}' ya cuenta con un pago completado.");
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
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("Pago registrado exitosamente con ID '{PaymentId}'.", payment.Id);

        await _eventPublisher.PublishAsync(
            "fixia.payments.events",
            "pago.creado",
            new
            {
                PaymentId = payment.Id,
                ServiceRequestId = payment.ServiceRequestId,
                ClientId = payment.ClientId,
                ProviderId = payment.ProviderId,
                Amount = payment.Amount,
                Status = payment.Status.ToString()
            },
            ct);

        return payment.ToResponse();
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(Guid paymentId, ProcessPaymentRequest? request = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Iniciando procesamiento de cobro para el pago '{PaymentId}'.", paymentId);

        var payment = await _paymentRepository.GetByIdAsync(paymentId, ct);
        if (payment == null)
            throw new PaymentNotFoundException(paymentId);

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

        if (gatewayResult.IsSuccess)
        {
            payment.MarkAsCompleted(gatewayResult.TransactionReference, gatewayResult.RawResponse);
            _logger.LogInformation("Pago '{PaymentId}' completado exitosamente con referencia '{Reference}'.",
                payment.Id, gatewayResult.TransactionReference);

            await _eventPublisher.PublishAsync(
                "fixia.payments.events",
                "pago.completado",
                new
                {
                    PaymentId = payment.Id,
                    ServiceRequestId = payment.ServiceRequestId,
                    ClientId = payment.ClientId,
                    ProviderId = payment.ProviderId,
                    Amount = payment.Amount,
                    TransactionReference = gatewayResult.TransactionReference,
                    Status = "Completed"
                },
                ct);
        }
        else
        {
            var failureMsg = gatewayResult.ErrorMessage ?? "Error desconocido en la pasarela de pagos.";
            payment.MarkAsFailed(failureMsg, gatewayResult.RawResponse);
            _logger.LogWarning("Pago '{PaymentId}' rechazado o fallido: {Reason}", payment.Id, failureMsg);

            await _eventPublisher.PublishAsync(
                "fixia.payments.events",
                "pago.fallido",
                new
                {
                    PaymentId = payment.Id,
                    ServiceRequestId = payment.ServiceRequestId,
                    ClientId = payment.ClientId,
                    ProviderId = payment.ProviderId,
                    Amount = payment.Amount,
                    FailureReason = failureMsg,
                    Status = "Failed"
                },
                ct);
        }

        _paymentRepository.Update(payment);
        await _unitOfWork.SaveChangesAsync(ct);

        return payment.ToResponse();
    }

    public async Task<PaymentResponse> GetByIdAsync(Guid paymentId, CancellationToken ct = default)
    {
        var payment = await _paymentRepository.GetByIdAsync(paymentId, ct);
        if (payment == null)
            throw new PaymentNotFoundException(paymentId);

        return payment.ToResponse();
    }

    public async Task<PaymentResponse> GetByServiceRequestIdAsync(Guid serviceRequestId, CancellationToken ct = default)
    {
        var payment = await _paymentRepository.GetByServiceRequestIdAsync(serviceRequestId, ct);
        if (payment == null)
            throw new PaymentDomainException($"No existe ningún pago asociado a la solicitud de servicio '{serviceRequestId}'.");

        return payment.ToResponse();
    }

    public async Task<PaymentResponse> RefundPaymentAsync(Guid paymentId, string? reason = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Iniciando solicitud de reembolso para el pago '{PaymentId}'.", paymentId);

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
        await _unitOfWork.SaveChangesAsync(ct);

        _logger.LogInformation("Reembolso del pago '{PaymentId}' completado exitosamente.", payment.Id);

        await _eventPublisher.PublishAsync(
            "fixia.payments.events",
            "pago.reembolsado",
            new
            {
                PaymentId = payment.Id,
                ServiceRequestId = payment.ServiceRequestId,
                Amount = payment.Amount,
                RefundReference = refundRef,
                Reason = reason,
                Status = "Refunded"
            },
            ct);

        return payment.ToResponse();
    }
}

