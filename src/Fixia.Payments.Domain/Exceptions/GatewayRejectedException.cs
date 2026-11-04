namespace Fixia.Payments.Domain.Exceptions;

/// <summary>La pasarela (simulada) rechazó la operación; el saldo no cambia (GC-482, GC-483). Se responde 402.</summary>
public class GatewayRejectedException : PaymentDomainException
{
    public GatewayRejectedException(string message) : base(message)
    {
    }
}
