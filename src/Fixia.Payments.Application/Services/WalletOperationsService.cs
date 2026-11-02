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
/// retira dos veces.
/// </summary>
public class WalletOperationsService : IWalletOperationsService
{
    public const string TopUpOperation = "wallet.top-up";
    public const string WithdrawalOperation = "wallet.withdrawal";

    private readonly IWalletRepository _wallets;
    private readonly IPaymentGateway _gateway;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;

    public WalletOperationsService(IWalletRepository wallets, IPaymentGateway gateway, IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork, TimeProvider clock)
    {
        _wallets = wallets;
        _gateway = gateway;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
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

        // La billetera se asegura fuera de la transacción de la operación.
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

    /// <summary>Huella del cuerpo normalizado: 50000, 50000.0 y 50000.00 son el mismo pedido.</summary>
    private static string Hash(decimal amount, string currency) =>
        IdempotencyKeys.Hash($"{amount.ToString("0.00", CultureInfo.InvariantCulture)}|{currency}");

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
