using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Sps.P2G.Gateway.Infrastructure.Security;

namespace Sps.P2G.Gateway.Infrastructure.RateLimiting;

public static class RateLimitPolicies
{
    public const string Authority = "authority-api";
    public const string SipsConnect = "sips-connect";
    public const string Dashboard = "dashboard";

    public static RateLimitPartition<string> FixedWindow(HttpContext context, string prefix, int permitLimit, GatewayRateLimitOptions options)
    {
        if (!options.Enabled)
        {
            return RateLimitPartition.GetNoLimiter($"{prefix}:disabled");
        }

        var key = GetPartitionKey(context, prefix);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, options.WindowSeconds)),
            QueueLimit = Math.Max(0, options.QueueLimit),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }

    private static string GetPartitionKey(HttpContext context, string prefix)
    {
        var clientId = AuthClaimReader.GetClientId(context.User);
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            return $"{prefix}:client:{clientId.ToUpperInvariant()}";
        }

        var apiKey = context.Request.Headers["X-Authority-Api-Key"].FirstOrDefault()
            ?? context.Request.Headers["X-Api-Key"].FirstOrDefault()
            ?? context.Request.Headers["X-Internal-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return $"{prefix}:key:{HashForPartition(apiKey)}";
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        return $"{prefix}:ip:{remoteIp ?? "unknown"}";
    }

    private static string HashForPartition(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 8);
    }
}
