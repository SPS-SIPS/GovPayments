namespace Sps.P2G.Gateway.Infrastructure.Observability;

public sealed class HealthOptions
{
    public int MaxPendingOutboxMessages { get; set; } = 10_000;
    public int MaxDeadLetterCallbacks { get; set; } = 100;
}
