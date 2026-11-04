using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Domain.Entities;

/// <summary>Reglas de los importes (GC-480): decimales exactos, positivos y con dos decimales como máximo.</summary>
public static class Money
{
    public const int Decimals = 2;

    public static void RequirePositive(decimal amount)
    {
        if (amount <= 0)
            throw new PaymentDomainException("El importe debe ser mayor que cero.");
        if (decimal.Round(amount, Decimals) != amount)
            throw new PaymentDomainException("El importe admite como máximo dos decimales.");
    }

    /// <summary>Redondeo comercial a dos decimales (0,005 sube).</summary>
    public static decimal Round(decimal amount) =>
        decimal.Round(amount, Decimals, MidpointRounding.AwayFromZero);
}
