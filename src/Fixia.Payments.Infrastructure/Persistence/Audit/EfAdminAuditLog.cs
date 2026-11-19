using System.Text.Json;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fixia.Payments.Infrastructure.Persistence.Audit;

/// <summary>
/// Registro de acciones del ADMIN sobre la tabla <c>admin_audit_events</c> (GC-535, contrato admin §5).
/// <see cref="Stage"/> usa el contexto de la petición, así que el registro de un reembolso se confirma o se revierte
/// junto con el reembolso. <see cref="RecordAsync"/> usa un contexto propio: lo pendiente de una operación que falló
/// nunca se cuela con el registro.
/// </summary>
public sealed class EfAdminAuditLog : IAdminAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PaymentsDbContext _context;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;

    public EfAdminAuditLog(PaymentsDbContext context, IServiceScopeFactory scopes, TimeProvider clock)
    {
        _context = context;
        _scopes = scopes;
        _clock = clock;
    }

    public void Stage(Guid adminId, AdminAction action, Guid? targetPaymentId, object? details,
        AdminAuditOutcome outcome) =>
        _context.AdminAuditEvents.Add(New(adminId, action, targetPaymentId, details, outcome));

    public async Task RecordAsync(Guid adminId, AdminAction action, Guid? targetPaymentId, object? details,
        AdminAuditOutcome outcome, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        context.AdminAuditEvents.Add(New(adminId, action, targetPaymentId, details, outcome));
        await context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AdminAuditEvent>> ListAsync(Guid? adminId, AdminAction? action,
        AdminListPosition? after, int take, CancellationToken ct = default)
    {
        var query = _context.AdminAuditEvents.AsNoTracking();
        if (adminId is { } admin)
            query = query.Where(e => e.AdminId == admin);
        if (action is { } a)
            query = query.Where(e => e.Action == a);
        if (after != null)
        {
            query = query.Where(e => e.OccurredAt < after.At
                                     || (e.OccurredAt == after.At && e.Id.CompareTo(after.Id) < 0));
        }

        return await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    private AdminAuditEvent New(Guid adminId, AdminAction action, Guid? targetPaymentId, object? details,
        AdminAuditOutcome outcome) =>
        new(adminId, action, targetPaymentId, details is null ? null : JsonSerializer.Serialize(details, Json),
            outcome, _clock.GetUtcNow().UtcDateTime);
}
