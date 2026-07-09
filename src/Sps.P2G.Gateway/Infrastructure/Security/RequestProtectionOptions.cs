namespace Sps.P2G.Gateway.Infrastructure.Security;

public sealed class RequestProtectionOptions
{
    public bool Enabled { get; set; } = true;
    public long MaxRequestBodyBytes { get; set; } = 1_048_576;
}
