namespace Sps.P2G.Gateway.Infrastructure.Database;

public sealed class DatabaseOptions
{
    public bool RunMigrationsOnStartup { get; set; }
    public int MigrationRetryCount { get; set; } = 10;
    public int MigrationRetryDelaySeconds { get; set; } = 3;
}
