using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Fixia.Payments.Application.Services;

public interface IServiceEventsHandler
{
    /// <summary>Aplica el efecto del evento una sola vez. Devuelve false si se ignoró (tipo ajeno o repetido).</summary>
    Task<bool> HandleAsync(ServiceEvent serviceEvent, CancellationToken ct = default);
}

/// <summary>
/// Reacciona a los hechos de ms-services con la billetera simulada (GC-484, GC-485), por coreografía y sin orquestador:
/// <list type="bullet">
/// <item><c>servicio.contratado</c>: registra el pago (precio + 15 % a cargo del cliente) y retiene el total en la
/// billetera del cliente → <c>pago.creado</c> y <c>pago.procesado</c>. Si no alcanza el saldo, el pago queda
/// <c>Failed</c> sin mover nada → <c>pago.fallido</c>; el cliente recarga y reintenta con <c>POST /{id}/process</c>.</item>
/// <item><c>servicio.cerrado</c>: cobra lo retenido; el prestador recibe el precio íntegro y Fixia la comisión →
/// <c>pago.completado</c>.</item>
/// <item><c>servicio.cierre_objetado</c>: no se cobra; el total sigue retenido.</item>
/// </list>
/// Todo el efecto (pago, billeteras, movimientos, eventos del Outbox y la marca del evento procesado) se guarda en un solo
/// <c>SaveChangesAsync</c>: o se aplica completo o no se aplica. Un evento repetido no tiene efecto.
/// </summary>
public class ServiceEventsHandler : IServiceEventsHandler
{
    private readonly IPaymentRepository _payments;
    private readonly IWalletRepository _wallets;
    private readonly ICommissionService _commission;
    private readonly IBusinessEventOutbox _outbox;
    private readonly IProcessedEventStore _processed;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;
    private readonly ILogger<ServiceEventsHandler> _logger;

    public ServiceEventsHandler(IPaymentRepository payments, IWalletRepository wallets, ICommissionService commission,
        IBusinessEventOutbox outbox, IProcessedEventStore processed, IUnitOfWork unitOfWork, TimeProvider clock,
        ILogger<ServiceEventsHandler> logger)
    {
        _payments = payments;
        _wallets = wallets;
        _commission = commission;
        _outbox = outbox;
        _processed = processed;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    public async Task<bool> HandleAsync(ServiceEvent serviceEvent, CancellationToken ct = default)
    {
        if (serviceEvent.EventType is not (ServiceEvent.Contracted or ServiceEvent.Closed or ServiceEvent.ClosureObjected))
            return false; // otros hechos del servicio no le interesan a pagos

        if (await _processed.IsProcessedAsync(serviceEvent.EventId, ct))
        {
            _logger.LogInformation("Evento {EventId} ({EventType}) ya procesado: se ignora la reentrega.",
                serviceEvent.EventId, serviceEvent.EventType);
            return false;
        }

        switch (serviceEvent.EventType)
        {
            case ServiceEvent.Contracted:
                await OnContractedAsync(serviceEvent, ct);
                break;
            case ServiceEvent.Closed:
                await OnClosedAsync(serviceEvent, ct);
                break;
            default:
                _logger.LogInformation("Cierre objetado en la solicitud {ServiceRequestId}: el pago sigue retenido.",
                    serviceEvent.DataGuid("serviceRequestId"));
                break;
        }

        _processed.Add(serviceEvent.EventId, serviceEvent.EventType, Now());
        await _unitOfWork.SaveChangesAsync(ct);
        return true;
    }

    private async Task OnContractedAsync(ServiceEvent e, CancellationToken ct)
    {
        var serviceRequestId = e.DataGuid("serviceRequestId");
        if (await _payments.GetByServiceRequestIdAsync(serviceRequestId, ct) is { } existing)
        {
            _logger.LogInformation("La solicitud {ServiceRequestId} ya tiene el pago {PaymentId}: no se crea otro.",
                serviceRequestId, existing.Id);
            return;
        }

        var (price, currency) = e.DataMoney("price");
        var (_, quote) = await _commission.QuoteNowAsync(price, currency, ct);
        var clientId = e.DataGuid("clientId");
        var payment = Payment.ForContract(serviceRequestId, e.DataGuid("contractId"), clientId,
            e.DataGuid("providerId"), quote);
        await _payments.AddAsync(payment, ct);
        await _outbox.EnqueueAsync(PaymentEvents.PaymentCreated(payment, e.Actor), ct);

        var clientWallet = await _wallets.GetByOwnerAsync(clientId, payment.Currency, ct);
        var (hold, failure) = WalletSettlement.TryHold(payment, clientWallet, Now());
        if (hold != null)
        {
            await _wallets.AddEntryAsync(hold, ct);
            await _outbox.EnqueueAsync(PaymentEvents.PaymentHeld(payment, e.Actor), ct);
            _logger.LogInformation("Pago {PaymentId} retenido ({Total} {Currency}) para la solicitud {ServiceRequestId}.",
                payment.Id, payment.Amount, payment.Currency, serviceRequestId);
        }
        else
        {
            payment.MarkAsFailed(failure!);
            await _outbox.EnqueueAsync(PaymentEvents.PaymentFailed(payment, e.Actor), ct);
            _logger.LogWarning("No se pudo retener el pago {PaymentId}: {Reason}", payment.Id, failure);
        }
    }

    private async Task OnClosedAsync(ServiceEvent e, CancellationToken ct)
    {
        var serviceRequestId = e.DataGuid("serviceRequestId");
        var payment = await _payments.GetByServiceRequestIdAsync(serviceRequestId, ct);
        if (payment is null || !payment.IsWalletPayment || payment.Status != PaymentStatus.Processing)
        {
            // Inconsistencia: se cerró un servicio sin pago retenido. No se inventan abonos; queda en el log para revisión.
            _logger.LogWarning(
                "Cierre de la solicitud {ServiceRequestId} sin pago retenido (pago: {PaymentId}, estado: {Status}); no se cobra nada.",
                serviceRequestId, payment?.Id, payment?.Status);
            return;
        }

        // Las billeteras se aseguran antes de tocar nada: crear la del prestador guarda por separado.
        var provider = await _wallets.GetOrCreateForUserAsync(payment.ProviderId, payment.Currency, Now(), ct);
        var client = await _wallets.GetByOwnerAsync(payment.ClientId, payment.Currency, ct)
                     ?? throw new InvalidOperationException($"El pago {payment.Id} está retenido pero el cliente no tiene billetera");
        var platform = await _wallets.GetByOwnerAsync(Wallet.PlatformOwnerId, payment.Currency, ct)
                       ?? throw new InvalidOperationException("No existe la cuenta de Fixia (la siembra la migración AddWallets)");

        foreach (var entry in WalletSettlement.Capture(payment, client, provider, platform, Now()))
            await _wallets.AddEntryAsync(entry, ct);
        _payments.Update(payment);
        await _outbox.EnqueueAsync(PaymentEvents.PaymentCompleted(payment, e.Actor), ct);
        _logger.LogInformation("Pago {PaymentId} cobrado al cerrar: prestador +{Price}, Fixia +{Commission}.",
            payment.Id, payment.BaseAmount, payment.CommissionAmount);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
