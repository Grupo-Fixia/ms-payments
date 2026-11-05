using System.Globalization;
using Fixia.Payments.Application.Common;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Application.Services;

public interface IWalletOperationsService
{
    Task<IdempotentResult<WalletOperationResponse>> TopUpAsync(Requester requester, string? idempotencyKey,
        WalletAmountRequest request, CancellationToken ct = default);

    Task<IdempotentResult<WalletOperationResponse>> WithdrawAsync(Requester requester, string? idempotencyKey,
        WalletAmountRequest request, CancellationToken ct = default);
}

/// <summary>
/// Recarga del cliente (GC-482) y retiro del técnico o empresa (GC-483), con la pasarela simulada. Ningún movimiento
/// es de dinero real. Las dos operaciones exigen <c>Idempotency-Key</c>: repetir la misma petición no recarga ni
/// retira dos veces. El retiro tiene además un máximo de retiros por día (<see cref="WithdrawalLimits"/>).
/// <para>
/// Dos operaciones simultáneas sobre la misma billetera chocan en el bloqueo optimista; en vez de devolver 409 al
/// usuario, la operación se reintenta desde cero (relee la billetera) hasta <see cref="MaxAttempts"/> veces (GC-501).
/// </para>
/// </summary>
public class WalletOperationsService : IWalletOperationsService
{
    public const string TopUpOperation = "wallet.top-up";
    public const string WithdrawalOperation = "wallet.withdrawal";
    public const int MaxAttempts = 10;

    private readonly IWalletRepository _wallets;
    private readonly IPaymentGateway _gateway;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;
    private readonly WithdrawalLimits _withdrawalLimits;

    public WalletOperationsService(IWalletRepository wallets, IPaymentGateway gateway, IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork, TimeProvider clock, WithdrawalLimits withdrawalLimits)
    {
        _wallets = wallets;
        _gateway = gateway;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _withdrawalLimits = withdrawalLimits;
    }

    public Task<IdempotentResult<WalletOperationResponse>> TopUpAsync(Requester requester, string? idempotencyKey,
        WalletAmountRequest request, CancellationToken ct = default)
    {
        if (!requester.Roles.Contains(Requester.RoleClient))
            throw new PaymentAccessDeniedException("Solo un cliente puede recargar su billetera.");

        return RunAsync(requester, idempotencyKey, request, TopUpOperation, async (wallet, amount, currency, token) =>
        {
            var approval = await _gateway.ProcessTopUpAsync(wallet.Id, amount, currency, token);
            if (!approval.IsSuccess)
                throw new GatewayRejectedException(approval.ErrorMessage ?? "La pasarela simulada rechazó la recarga.");
            return (WalletEntryType.TopUp, approval.TransactionReference);
        }, ct);
    }

    public Task<IdempotentResult<WalletOperationResponse>> WithdrawAsync(Requester requester, string? idempotencyKey,
        WalletAmountRequest request, CancellationToken ct = default)
    {
        if (!requester.Roles.Contains(Requester.RoleProfessional) && !requester.Roles.Contains(Requester.RoleCompany))
            throw new PaymentAccessDeniedException("Solo un técnico o una empresa puede retirar saldo.");

        return RunAsync(requester, idempotencyKey, request, WithdrawalOperation, async (wallet, amount, currency, token) =>
        {
            // Se comprueba antes de llamar a la pasarela: no se "paga" lo que no hay. El retenido nunca se retira.
            if (wallet.AvailableBalance < amount)
                throw new InsufficientBalanceException(
                    $"Saldo disponible insuficiente: hay {wallet.AvailableBalance:0.00} {wallet.Currency} y se piden {amount:0.00}.");

            // Tope de retiros por día. Un reintento con la misma Idempotency-Key no llega aquí (se repite la respuesta
            // guardada), así que no gasta cupo. Dos retiros simultáneos no pasan los dos: el segundo choca con el
            // bloqueo optimista de la billetera al guardar.
            var (dayStart, dayEnd) = _withdrawalLimits.DayOf(Now());
            var today = await _wallets.CountEntriesAsync(wallet.Id, WalletEntryType.Withdrawal, dayStart, dayEnd, token);
            if (today >= _withdrawalLimits.MaxPerDay)
                throw new WithdrawalLimitExceededException(
                    $"Ya hiciste los {_withdrawalLimits.MaxPerDay} retiros permitidos hoy. Podrás retirar de nuevo mañana.");

            var approval = await _gateway.ProcessPayoutAsync(wallet.Id, amount, currency, token);
            if (!approval.IsSuccess)
                throw new GatewayRejectedException(approval.ErrorMessage ?? "La pasarela simulada rechazó el retiro.");
            return (WalletEntryType.Withdrawal, approval.TransactionReference);
        }, ct);
    }

    private async Task<IdempotentResult<WalletOperationResponse>> RunAsync(Requester requester, string? idempotencyKey,
        WalletAmountRequest request, string operation,
        Func<Wallet, decimal, string, CancellationToken, Task<(WalletEntryType Type, string Reference)>> authorize,
        CancellationToken ct)
    {
        var key = IdempotencyKeys.Validate(idempotencyKey);
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? Wallet.DefaultCurrency
            : request.Currency.Trim().ToUpperInvariant();
        if (currency != Wallet.DefaultCurrency)
            throw new InvalidQueryException($"Por ahora solo se maneja {Wallet.DefaultCurrency}.");
        Money.RequirePositive(request.Amount);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // La billetera se asegura fuera de la transacción de la operación y se relee en cada intento.
                var wallet = await _wallets.GetOrCreateForUserAsync(requester.UserId, currency, Now(), ct);

                return await _idempotency.RunAsync(requester.UserId, operation, key, Hash(request.Amount, currency),
                    async token =>
                    {
                        var (type, reference) = await authorize(wallet, request.Amount, currency, token);
                        var entry = wallet.Apply(type, request.Amount, Now(), reference: reference);
                        await _wallets.AddEntryAsync(entry, token);
                        await _unitOfWork.SaveChangesAsync(token);

                        return new WalletOperationResponse(entry.Id, entry.Type, entry.Amount, wallet.Currency,
                            wallet.AvailableBalance, wallet.HeldBalance, reference, entry.CreatedAt);
                    }, ct);
            }
            catch (Exception ex) when (_unitOfWork.IsConcurrencyConflict(ex))
            {
                // La transacción ya se deshizo (incluida la clave de idempotencia): nada quedó a medias.
                _unitOfWork.DiscardChanges();
                if (attempt >= MaxAttempts)
                    throw new WalletBusyException();
                await Task.Delay(Backoff(attempt), ct);
            }
        }
    }

    /// <summary>Espera corta, creciente y con azar, para que las operaciones que chocaron no vuelvan a chocar.</summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(5, 25) * attempt);

    /// <summary>Huella del cuerpo normalizado: 50000, 50000.0 y 50000.00 son el mismo pedido.</summary>
    private static string Hash(decimal amount, string currency) =>
        IdempotencyKeys.Hash($"{amount.ToString("0.00", CultureInfo.InvariantCulture)}|{currency}");

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
