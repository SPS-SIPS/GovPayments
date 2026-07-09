using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Sps.P2G.Gateway.Infrastructure.Database;

public static class DatabaseStartupExtensions
{
    public static async Task ApplyDatabaseMigrationsAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.RunMigrationsOnStartup)
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILogger<GatewayDbContext>>();
        var retryCount = Math.Max(1, options.MigrationRetryCount);
        var retryDelay = TimeSpan.FromSeconds(Math.Max(1, options.MigrationRetryDelaySeconds));

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                await db.Database.MigrateAsync();
                return;
            }
            catch (Exception ex) when (attempt < retryCount)
            {
                logger.LogWarning(ex, "Database migration attempt {Attempt}/{RetryCount} failed. Retrying in {DelaySeconds} seconds.", attempt, retryCount, retryDelay.TotalSeconds);
                await Task.Delay(retryDelay);
            }
        }

        await using var finalScope = app.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        await finalDb.Database.MigrateAsync();
    }
}
