namespace Fixia.Payments.Domain.Exceptions;

/// <summary>No hay una tarifa de comisión vigente para ese instante (GC-480). Se responde 409: es configuración.</summary>
public class CommissionRuleNotFoundException : PaymentDomainException
{
    public CommissionRuleNotFoundException(DateTime instant)
        : base($"No hay una tarifa de comisión vigente para {instant:yyyy-MM-dd HH:mm} UTC.")
    {
    }
}
