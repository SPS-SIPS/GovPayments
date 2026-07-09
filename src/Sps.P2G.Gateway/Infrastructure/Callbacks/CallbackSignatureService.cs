using System.Security.Cryptography;
using System.Text;

namespace Sps.P2G.Gateway.Infrastructure.Callbacks;

public static class CallbackSignatureService
{
    public static string Sign(string secret, string timestamp, Guid eventId, string payloadJson)
    {
        var input = $"{timestamp}.{eventId}.{payloadJson}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
