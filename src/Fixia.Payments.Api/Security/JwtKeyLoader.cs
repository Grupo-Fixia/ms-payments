using System.Security.Cryptography;

namespace Fixia.Payments.Api.Security;

/// <summary>
/// Carga la clave pública RSA con la que se verifican los tokens de ms-users (GC-477). Mismo comportamiento que el
/// <c>JwtKeyLoader</c> de ms-services.
/// </summary>
public static class JwtKeyLoader
{
    private const int GeneratedKeySize = 2048;

    /// <summary>Clave pública X.509 en formato PEM (con o sin las líneas <c>-----BEGIN/END-----</c>).</summary>
    public static RSA FromPem(string pem)
    {
        try
        {
            // Las variables de entorno suelen traer los saltos de línea como "\n" literales.
            var normalized = pem.Replace("\\n", "\n");
            var body = string.Concat(normalized
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal)));

            var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(body), out _);
            return rsa;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // No se incluye el contenido de la clave en el mensaje.
            throw new InvalidOperationException("JWT_PUBLIC_KEY no tiene un formato PEM válido (X.509)");
        }
    }

    /// <summary>
    /// Clave efímera, solo para Development y pruebas: ningún token real de ms-users se podrá verificar con ella
    /// (todas las peticiones protegidas responden 401), pero permite arrancar el servicio sin configuración.
    /// </summary>
    public static RSA Generate() => RSA.Create(GeneratedKeySize);
}
