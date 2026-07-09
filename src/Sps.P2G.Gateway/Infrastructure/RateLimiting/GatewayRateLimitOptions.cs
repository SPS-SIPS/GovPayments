namespace Sps.P2G.Gateway.Infrastructure.RateLimiting;

public sealed class GatewayRateLimitOptions
{
    public bool Enabled { get; set; } = true;
    public int AuthorityPermitLimit { get; set; } = 300;
    public int SipsConnectPermitLimit { get; set; } = 600;
    public int DashboardPermitLimit { get; set; } = 120;
    public int WindowSeconds { get; set; } = 60;
    public int QueueLimit { get; set; } = 0;
}
