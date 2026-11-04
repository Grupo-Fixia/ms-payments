namespace Fixia.Payments.Domain.Exceptions;

/// <summary>Un parámetro de consulta no se puede interpretar (límite, cursor, importe). Se responde 400.</summary>
public class InvalidQueryException : PaymentDomainException
{
    public InvalidQueryException(string message) : base(message)
    {
    }
}
