using System.Net;
using System.Net.Sockets;

namespace Sps.P2G.Gateway.Infrastructure.Callbacks;

public static class CallbackUrlValidator
{
    public static CallbackUrlValidationResult Validate(string? callbackUrl, CallbackSecurityOptions options)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return CallbackUrlValidationResult.Invalid("callbackUrl must be an absolute HTTP(S) URL");
        }

        if (options.RequireHttps && uri.Scheme != Uri.UriSchemeHttps)
        {
            return CallbackUrlValidationResult.Invalid("callbackUrl must use HTTPS");
        }

        if (!options.AllowPrivateNetworkTargets && IsPrivateTarget(uri.Host))
        {
            return CallbackUrlValidationResult.Invalid("callbackUrl must not target localhost or private network addresses");
        }

        return CallbackUrlValidationResult.Valid(uri);
    }

    private static bool IsPrivateTarget(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IsPrivateAddress(address);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal ||
                   address.IsIPv6SiteLocal ||
                   address.IsIPv6Multicast ||
                   (bytes[0] & 0xfe) == 0xfc;
        }

        var octets = address.GetAddressBytes();
        return octets[0] == 0 ||
               octets[0] == 10 ||
               octets[0] == 127 ||
               (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127) ||
               (octets[0] == 169 && octets[1] == 254) ||
               (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
               (octets[0] == 192 && octets[1] == 168) ||
               (octets[0] == 198 && (octets[1] == 18 || octets[1] == 19)) ||
               octets[0] >= 224;
    }
}

public sealed record CallbackUrlValidationResult(bool IsValid, Uri? Uri, string? Error)
{
    public static CallbackUrlValidationResult Valid(Uri uri) => new(true, uri, null);

    public static CallbackUrlValidationResult Invalid(string error) => new(false, null, error);
}
