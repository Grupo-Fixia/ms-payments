using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Security;

/// <summary>
/// Cuerpos RFC 7807 de 401 y 403, iguales a los de ms-users y ms-services. El 401 no explica el motivo (token
/// ausente, vencido, de otro emisor o mal firmado) para no dar pistas a quien prueba tokens.
/// </summary>
public static class ProblemResponses
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return WriteAsync(context, StatusCodes.Status401Unauthorized, "No autenticado", "Se requiere autenticación válida");
    }

    public static Task WriteForbiddenAsync(HttpContext context) =>
        WriteAsync(context, StatusCodes.Status403Forbidden, "Acceso denegado",
            "No tiene permisos para realizar esta operación");

    private static Task WriteAsync(HttpContext context, int status, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
    }
}
