namespace Fixia.Payments.Api.Middlewares;

/// <summary>
/// Saldos, movimientos y pagos no deben quedar en la caché del navegador ni de un proxy (GC-512, SEG-10). Toda respuesta
/// de <c>/api/payments/**</c> y <c>/internal/**</c>, también las de error, lleva las mismas cabeceras que ya envían
/// ms-users y ms-services.
/// </summary>
public sealed class NoStoreCacheMiddleware
{
    public const string CacheControl = "no-cache, no-store, max-age=0, must-revalidate";

    private readonly RequestDelegate _next;

    public NoStoreCacheMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/payments")
            || context.Request.Path.StartsWithSegments("/internal"))
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.CacheControl = CacheControl;
                headers.Pragma = "no-cache";
                headers.Expires = "0";
                return Task.CompletedTask;
            });
        }

        return _next(context);
    }
}
