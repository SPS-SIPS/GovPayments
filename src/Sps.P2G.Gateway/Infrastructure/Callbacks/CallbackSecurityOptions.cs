namespace Sps.P2G.Gateway.Infrastructure.Callbacks;

public sealed class CallbackSecurityOptions
{
    public bool RequireHttps { get; set; } = true;
    public bool AllowPrivateNetworkTargets { get; set; }
}
