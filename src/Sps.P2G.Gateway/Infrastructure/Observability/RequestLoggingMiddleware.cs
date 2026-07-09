using System.Diagnostics;

namespace Sps.P2G.Gateway.Infrastructure.Observability;

public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
                context.Request.Method,
                context.Request.Path.Value ?? "",
                context.Response.StatusCode,
                elapsedMilliseconds);
        }
    }
}
