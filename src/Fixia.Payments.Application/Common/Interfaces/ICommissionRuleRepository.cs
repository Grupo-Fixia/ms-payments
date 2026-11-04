using Fixia.Payments.Domain.Entities;

namespace Fixia.Payments.Application.Common.Interfaces;

/// <summary>Tarifas de comisión con vigencia (GC-480).</summary>
public interface ICommissionRuleRepository
{
    /// <summary>La regla vigente en ese instante (la de inicio más reciente), o null si no hay ninguna.</summary>
    Task<CommissionRule?> GetValidAtAsync(DateTime instant, CancellationToken ct = default);
}
