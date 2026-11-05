using System.Net;
using System.Text.Json;
using Fixia.Payments.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.AspNetCore.Mvc;

namespace Fixia.Payments.Api.Middlewares;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var statusCode = HttpStatusCode.InternalServerError;
        var title = "Ha ocurrido un error inesperado en el servidor.";
        var detail = exception.Message;

        switch (exception)
        {
            case PaymentNotFoundException notFound:
                statusCode = HttpStatusCode.NotFound;
                title = "Pago no encontrado";
                detail = notFound.Message;
                _logger.LogWarning("Pago no encontrado: {Message}", notFound.Message);
                break;

            case InvalidPaymentStatusException invalidStatus:
                statusCode = HttpStatusCode.Conflict;
                title = "Transición de estado de pago inválida";
                detail = invalidStatus.Message;
                _logger.LogWarning("Conflicto de estado de pago: {Message}", invalidStatus.Message);
                break;

            case PaymentAccessDeniedException accessDenied:
                statusCode = HttpStatusCode.Forbidden;
                title = "Acceso denegado";
                detail = accessDenied.Message;
                _logger.LogWarning("Operación de pago no permitida: {Message}", accessDenied.Message);
                break;

            // GC-480: el movimiento dejaría un saldo negativo, o no hay tarifa de comisión vigente.
            case InsufficientBalanceException or CommissionRuleNotFoundException:
                statusCode = HttpStatusCode.Conflict;
                title = exception is InsufficientBalanceException ? "Saldo insuficiente" : "Sin tarifa de comisión vigente";
                detail = exception.Message;
                _logger.LogWarning("Conflicto de billetera: {Message}", exception.Message);
                break;

            // GC-501: la billetera siguió cambiando por operaciones simultáneas incluso después de reintentar.
            case WalletBusyException busy:
                statusCode = HttpStatusCode.Conflict;
                title = "Billetera ocupada";
                detail = busy.Message;
                _logger.LogWarning("Billetera ocupada tras reintentar en {Path}", context.Request.Path);
                break;

            // GC-483: el técnico o empresa ya hizo todos los retiros permitidos en el día.
            case WithdrawalLimitExceededException limit:
                statusCode = HttpStatusCode.Conflict;
                title = "Límite de retiros alcanzado";
                detail = limit.Message;
                _logger.LogWarning("Límite diario de retiros alcanzado en {Path}", context.Request.Path);
                break;

            // GC-482/483: la misma Idempotency-Key con otro cuerpo, o la pasarela simulada rechazó.
            case IdempotencyKeyReuseException reuse:
                statusCode = HttpStatusCode.UnprocessableEntity;
                title = "Idempotency-Key reutilizada";
                detail = reuse.Message;
                _logger.LogWarning("Idempotency-Key reutilizada con otro cuerpo en {Path}", context.Request.Path);
                break;

            case GatewayRejectedException rejected:
                statusCode = HttpStatusCode.PaymentRequired;
                title = "Operación rechazada por la pasarela";
                detail = rejected.Message;
                _logger.LogWarning("Pasarela simulada rechazó la operación en {Path}", context.Request.Path);
                break;

            case InvalidQueryException invalidQuery:
                statusCode = HttpStatusCode.BadRequest;
                title = "Parámetros de consulta no válidos";
                detail = invalidQuery.Message;
                break;

            case PaymentConflictException conflict:
                statusCode = HttpStatusCode.Conflict;
                title = "Conflicto con un pago existente";
                detail = conflict.Message;
                _logger.LogWarning("Conflicto de pago: {Message}", conflict.Message);
                break;

            // Otra operación cambió el mismo pago al mismo tiempo (bloqueo optimista, GC-477).
            case DbUpdateConcurrencyException:
                statusCode = HttpStatusCode.Conflict;
                title = "Modificación concurrente";
                detail = "El pago cambió mientras se procesaba la solicitud. Consúltelo de nuevo y reintente.";
                _logger.LogWarning("Conflicto de concurrencia sobre un pago en {Path}", context.Request.Path);
                break;

            // Restricción única violada por dos creaciones simultáneas de la misma solicitud (GC-477).
            case DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }:
                statusCode = HttpStatusCode.Conflict;
                title = "Conflicto con un pago existente";
                detail = "La solicitud de servicio ya tiene un pago registrado.";
                _logger.LogWarning("Violación de unicidad al guardar un pago en {Path}", context.Request.Path);
                break;

            case PaymentDomainException domainEx:
                statusCode = HttpStatusCode.BadRequest;
                title = "Error de validación de negocio";
                detail = domainEx.Message;
                _logger.LogWarning("Error de dominio: {Message}", domainEx.Message);
                break;

            default:
                _logger.LogError(exception, "Excepción no controlada procesando solicitud en {Path}: {Message}",
                    context.Request.Path, exception.Message);
                detail = "Ocurrió un error interno procesando su solicitud.";
                break;
        }

        var problemDetails = new ProblemDetails
        {
            Status = (int)statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = (int)statusCode;

        var json = JsonSerializer.Serialize(problemDetails, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }
}

