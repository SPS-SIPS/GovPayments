namespace Sps.P2G.Gateway.Infrastructure.Security;

public sealed class AuthOptions
{
    public bool AllowAuthorityApiKeyFallback { get; set; } = true;
    public bool AllowSipsConnectInternalKeyFallback { get; set; } = true;
}
