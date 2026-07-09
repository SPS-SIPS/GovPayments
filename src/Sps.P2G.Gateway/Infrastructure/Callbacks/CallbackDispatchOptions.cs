namespace Sps.P2G.Gateway.Infrastructure.Callbacks;

public sealed class CallbackDispatchOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 25;
    public int MaxAttempts { get; set; } = 8;
    public int RequestTimeoutSeconds { get; set; } = 10;
}
