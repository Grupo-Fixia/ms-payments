namespace Fixia.Payments.Domain.Exceptions;

/// <summary>
/// La billetera siguió cambiando por otras operaciones simultáneas y la recarga o el retiro no se pudo aplicar ni
/// después de reintentar (GC-501). No se aplicó nada: el cliente puede reintentar con la misma Idempotency-Key. 409.
/// </summary>
public class WalletBusyException : PaymentDomainException
{
    public WalletBusyException()
        : base("La billetera cambió mientras se procesaba la operación y no se aplicó nada. Inténtalo de nuevo.")
    {
    }
}
