namespace Sps.P2G.Gateway.Infrastructure.SipsConnect;

public sealed class SipsConnectClientOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } = "";
    public string PaymentStatusPath { get; set; } = "/api/v1/payments/status";
    public int RequestTimeoutSeconds { get; set; } = 30;
    public bool RequireHttps { get; set; } = true;
    public bool AllowInvalidServerCertificate { get; set; }
}
