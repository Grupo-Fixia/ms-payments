using System.Security.Cryptography;
using System.Text;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Application.Common;

/// <summary>Reglas comunes de la cabecera <c>Idempotency-Key</c> (GC-482, GC-483, GC-487).</summary>
public static class IdempotencyKeys
{
    public const int MaxLength = 100;

    /// <exception cref="InvalidQueryException">Si falta o no tiene la forma esperada (400).</exception>
    public static string Validate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidQueryException("La cabecera Idempotency-Key es obligatoria (por ejemplo, un UUID nuevo por operación).");
        var trimmed = key.Trim();
        if (trimmed.Length > MaxLength || trimmed.Any(c => c < 0x21 || c > 0x7E))
            throw new InvalidQueryException(
                $"La Idempotency-Key debe tener hasta {MaxLength} caracteres visibles, sin espacios.");
        return trimmed;
    }

    /// <summary>Huella SHA-256 del cuerpo ya normalizado.</summary>
    public static string Hash(string normalizedRequest) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRequest))).ToLowerInvariant();
}
