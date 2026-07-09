namespace Sps.P2G.Gateway.Infrastructure.Secrets;

public sealed class OpenBaoOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string KvMount { get; set; } = "secret";
    public string SecretPath { get; set; } = "sps-p2g-gateway/runtime";
    public int RequestTimeoutSeconds { get; set; } = 10;
}
