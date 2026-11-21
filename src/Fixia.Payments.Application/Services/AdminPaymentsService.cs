using System.Text;
using Fixia.Payments.Application.Common.Events;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Application.Common.Security;
using Fixia.Payments.Application.DTOs;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Application.Services;

public interface IAdminPaymentsService
{
    Task<AdminPaymentPage> ListPaymentsAsync(Requester admin, string? status, string? holdStatus,
        Guid? serviceRequestId, Guid? userId, int? limit, string? cursor, CancellationToken ct = default);

    Task<AdminAuditPage> ListAuditEventsAsync(Requester admin, Guid? adminId, string? action, int? limit,
        string? cursor, CancellationToken ct = default);

    /// <summary>Registra que el ADMIN consultó un pago (§4.2): <c>OK</c> o <c>NOT_FOUND</c>.</summary>
    Task RecordViewAsync(Requester admin, Guid? paymentId, bool found, object? details, CancellationToken ct = default);
}

/// <summary>
/// Consultas del ADMIN sobre pagos (GC-535, contrato <c>ms-users/docs/contracts/admin.md</c> §4). Solo leen; cada
/// consulta queda registrada en <see cref="IAdminAuditLog"/>.
/// </summary>
public sealed class AdminPaymentsService : IAdminPaymentsService
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    private readonly IPaymentRepository _payments;
    private readonly IAdminAuditLog _audit;

    public AdminPaymentsService(IPaymentRepository payments, IAdminAuditLog audit)
    {
        _payments = payments;
        _audit = audit;
    }

    public async Task<AdminPaymentPage> ListPaymentsAsync(Requester admin, string? status, string? holdStatus,
        Guid? serviceRequestId, Guid? userId, int? limit, string? cursor, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var filter = new AdminPaymentFilter(
            ParseEnum<PaymentStatus>(status, "status"),
            ParseEnum<WalletHoldStatus>(holdStatus, "holdStatus"),
            serviceRequestId,
            userId);
        var take = Limit(limit);
        var after = cursor is null ? null : AdminCursor.Decode(cursor);

        var rows = (await _payments.ListForAdminAsync(filter, after, take + 1, ct)).ToList();
        var next = NextCursor(rows, take, p => new AdminListPosition(p.CreatedAt, p.Id));

        await _audit.RecordAsync(admin.UserId, AdminAction.PAYMENT_LIST, null, new Dictionary<string, object?>
        {
            ["status"] = filter.Status?.ToString(),
            ["holdStatus"] = filter.HoldStatus?.ToString(),
            ["serviceRequestId"] = filter.ServiceRequestId,
            ["userId"] = filter.UserId,
            ["limit"] = take
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), AdminAuditOutcome.OK, ct);

        return new AdminPaymentPage(rows.Select(p => ToAdminResponse(p, admin)).ToList(), next);
    }

    public async Task<AdminAuditPage> ListAuditEventsAsync(Requester admin, Guid? adminId, string? action,
        int? limit, string? cursor, CancellationToken ct = default)
    {
        RequireAdmin(admin);
        var parsedAction = ParseEnum<AdminAction>(action, "action");
        var take = Limit(limit);
        var after = cursor is null ? null : AdminCursor.Decode(cursor);

        // Se lee antes de registrar esta consulta, para que la página no se incluya a sí misma.
        var rows = (await _audit.ListAsync(adminId, parsedAction, after, take + 1, ct)).ToList();
        var next = NextCursor(rows, take, e => new AdminListPosition(e.OccurredAt, e.Id));

        var details = new Dictionary<string, object?> { ["limit"] = take };
        if (adminId is not null)
            details["adminId"] = adminId;
        if (parsedAction is not null)
            details["action"] = parsedAction.ToString();
        await _audit.RecordAsync(admin.UserId, AdminAction.AUDIT_LIST, null, details, AdminAuditOutcome.OK, ct);

        return new AdminAuditPage(rows.Select(AdminAuditEventResponse.From).ToList(), next);
    }

    public Task RecordViewAsync(Requester admin, Guid? paymentId, bool found, object? details,
        CancellationToken ct = default) =>
        _audit.RecordAsync(admin.UserId, AdminAction.PAYMENT_VIEW, paymentId, details,
            found ? AdminAuditOutcome.OK : AdminAuditOutcome.NOT_FOUND, ct);

    /// <summary>
    /// Qué haría hoy el reembolso (GC-487): retenido → liberar, cobrado → revertir, pasarela cobrada → reembolsar por
    /// la pasarela. En cualquier otro estado no procede.
    /// </summary>
    public static string? RefundKindOf(Payment payment) =>
        PaymentMappings.HoldStatusOf(payment) switch
        {
            WalletHoldStatus.Held => PaymentEvents.RefundKinds.Release,
            WalletHoldStatus.Captured => PaymentEvents.RefundKinds.Reversal,
            WalletHoldStatus.NotApplicable when payment.Status == PaymentStatus.Completed =>
                PaymentEvents.RefundKinds.Gateway,
            _ => null
        };

    private static AdminPaymentResponse ToAdminResponse(Payment payment, Requester admin) =>
        new(payment.ToResponse(admin), RefundKindOf(payment));

    private static void RequireAdmin(Requester requester)
    {
        if (!requester.IsAdmin)
            throw new PaymentAccessDeniedException("Solo un administrador de Fixia puede hacer esta consulta.");
    }

    private static int Limit(int? limit)
    {
        if (limit is null)
            return DefaultLimit;
        if (limit < 1 || limit > MaxLimit)
            throw new InvalidQueryException($"El límite debe estar entre 1 y {MaxLimit}.");
        return limit.Value;
    }

    private static TEnum? ParseEnum<TEnum>(string? value, string field) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        // Solo nombres, no números: «5» no debe valer como estado.
        if (!value.Trim().All(c => char.IsLetter(c) || c == '_')
            || !Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed))
            throw new InvalidQueryException($"El valor de {field} no es válido.");
        return parsed;
    }

    private static string? NextCursor<T>(List<T> rows, int take, Func<T, AdminListPosition> positionOf)
    {
        if (rows.Count <= take)
            return null;
        rows.RemoveRange(take, rows.Count - take);
        return AdminCursor.Encode(positionOf(rows[^1]));
    }
}

/// <summary>Cursor opaco de las listas del ADMIN: fecha e id del último elemento, en base64url.</summary>
public static class AdminCursor
{
    public static string Encode(AdminListPosition position)
    {
        var raw = $"{position.At.Ticks}:{position.Id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <exception cref="InvalidQueryException">Si el texto no salió de esta API.</exception>
    public static AdminListPosition Decode(string cursor)
    {
        try
        {
            var base64 = cursor.Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], out var ticks)
                && ticks > DateTime.MinValue.Ticks && ticks < DateTime.MaxValue.Ticks
                && Guid.TryParseExact(parts[1], "N", out var id))
            {
                return new AdminListPosition(new DateTime(ticks, DateTimeKind.Utc), id);
            }
        }
        catch (FormatException)
        {
            // cae al error común
        }

        throw new InvalidQueryException("El cursor no es válido.");
    }
}
