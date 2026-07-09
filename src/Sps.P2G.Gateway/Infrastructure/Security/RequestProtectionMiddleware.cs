using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Sps.P2G.Gateway.Infrastructure.Security;

public sealed class RequestProtectionMiddleware(RequestDelegate next, IOptions<RequestProtectionOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestOptions = options.Value;
        if (!requestOptions.Enabled)
        {
            await next(context);
            return;
        }

        var maxBodyBytes = Math.Max(1, requestOptions.MaxRequestBodyBytes);
        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is not null && !bodySizeFeature.IsReadOnly)
        {
            bodySizeFeature.MaxRequestBodySize = maxBodyBytes;
        }

        if (context.Request.ContentLength > maxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "payload_too_large",
                maxRequestBodyBytes = maxBodyBytes
            });
            return;
        }

        await next(context);
    }
}
