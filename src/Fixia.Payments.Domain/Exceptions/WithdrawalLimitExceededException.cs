namespace Fixia.Payments.Domain.Exceptions;

/// <summary>
/// El técnico o empresa ya hizo todos los retiros que se permiten en el día (GC-483). No se llama a la pasarela ni se
/// mueve saldo. Se responde 409.
/// </summary>
public class WithdrawalLimitExceededException : PaymentDomainException
{
    public WithdrawalLimitExceededException(string message) : base(message)
    {
    }
}
