using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace Fixia.Payments.Api.Middlewares;

/// <summary>
/// Registra métricas HTTP en formato Prometheus texto 0.0.4 (<c>http_server_requests_seconds_count</c> y
/// <c>http_server_requests_seconds_sum</c>) con la plantilla de ruta de ASP.NET Core como etiqueta <c>uri</c>,
/// evitando <c>uri="UNKNOWN"</c>, y las expone en <c>/metrics</c> y <c>/actuator/prometheus</c>.
/// </summary>
public sealed class PrometheusMetricsMiddleware
{
    private static readonly Regex UuidSegment = new(
        @"(?<=/)[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?=/|$)",
        RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<(string Method, string Uri, int Status), MetricEntry> Entries = new();

    private readonly RequestDelegate _next;

    public PrometheusMetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (HttpMethods.IsGet(context.Request.Method) &&
            (path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/actuator/prometheus", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            await context.Response.WriteAsync(RenderPrometheusText(), context.RequestAborted);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var uri = ResolveUriTemplate(context, path);
            var key = (context.Request.Method, uri, context.Response.StatusCode);
            var entry = Entries.GetOrAdd(key, static _ => new MetricEntry());
            entry.Record(sw.Elapsed.TotalSeconds);
        }
    }

    public static string ResolveUriTemplate(HttpContext context, string rawPath)
    {
        if (context.GetEndpoint() is RouteEndpoint routeEndpoint &&
            !string.IsNullOrWhiteSpace(routeEndpoint.RoutePattern.RawText))
        {
            var raw = routeEndpoint.RoutePattern.RawText.TrimStart('/');
            return "/" + raw;
        }

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "/";
        }

        return UuidSegment.Replace(rawPath, "{id}");
    }

    public static string RenderPrometheusText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# HELP http_server_requests_seconds Duration of HTTP server requests in seconds");
        sb.AppendLine("# TYPE http_server_requests_seconds summary");

        foreach (var kvp in Entries.OrderBy(k => k.Key.Uri).ThenBy(k => k.Key.Method).ThenBy(k => k.Key.Status))
        {
            var (method, uri, status) = kvp.Key;
            var outcome = status switch
            {
                >= 200 and < 300 => "SUCCESS",
                >= 400 and < 500 => "CLIENT_ERROR",
                >= 500 => "SERVER_ERROR",
                _ => "INFORMATIONAL"
            };
            var (count, sum) = kvp.Value.Snapshot();
            var labels = $"method=\"{Escape(method)}\",outcome=\"{outcome}\",status=\"{status}\",uri=\"{Escape(uri)}\"";
            sb.Append("http_server_requests_seconds_count{").Append(labels).Append("} ")
              .AppendLine(count.ToString(CultureInfo.InvariantCulture));
            sb.Append("http_server_requests_seconds_sum{").Append(labels).Append("} ")
              .AppendLine(sum.ToString("F6", CultureInfo.InvariantCulture));
        }

        var proc = Process.GetCurrentProcess();
        sb.AppendLine("# HELP process_working_set_bytes Process working set memory in bytes");
        sb.AppendLine("# TYPE process_working_set_bytes gauge");
        sb.Append("process_working_set_bytes ").AppendLine(proc.WorkingSet64.ToString(CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class MetricEntry
    {
        private long _count;
        private long _sumMicros;

        public void Record(double seconds)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sumMicros, (long)(seconds * 1_000_000));
        }

        public (long Count, double SumSeconds) Snapshot()
        {
            var c = Interlocked.Read(ref _count);
            var s = Interlocked.Read(ref _sumMicros) / 1_000_000.0;
            return (c, s);
        }
    }
}
