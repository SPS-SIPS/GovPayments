using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.Observability;

namespace Sps.P2G.Gateway.Modules.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new
        {
            status = "Healthy",
            service = "Sps.P2G.Gateway"
        }))
            .WithName("HealthLive")
            .WithTags("Health");

        app.MapGet("/health/ready", async (
            GatewayDbContext db,
            IOptions<HealthOptions> options,
            CancellationToken cancellationToken) =>
        {
            var checks = new Dictionary<string, object>();
            var databaseHealthy = await CanConnectAsync(db, cancellationToken);
            checks["database"] = new
            {
                status = databaseHealthy ? "Healthy" : "Unhealthy",
                provider = db.Database.ProviderName
            };

            var pendingOutbox = databaseHealthy
                ? await db.OutboxMessages.CountAsync(x => x.Status == "Pending", cancellationToken)
                : 0;
            var deadLetterCallbacks = databaseHealthy
                ? await db.CallbackDeliveries.CountAsync(x => x.Status == "DeadLetter", cancellationToken)
                : 0;

            var outboxHealthy = pendingOutbox <= options.Value.MaxPendingOutboxMessages;
            var callbackHealthy = deadLetterCallbacks <= options.Value.MaxDeadLetterCallbacks;

            checks["outbox"] = new
            {
                status = outboxHealthy ? "Healthy" : "Unhealthy",
                pending = pendingOutbox,
                maxPending = options.Value.MaxPendingOutboxMessages
            };
            checks["callbacks"] = new
            {
                status = callbackHealthy ? "Healthy" : "Unhealthy",
                deadLetter = deadLetterCallbacks,
                maxDeadLetter = options.Value.MaxDeadLetterCallbacks
            };

            var healthy = databaseHealthy && outboxHealthy && callbackHealthy;
            return Results.Json(new
            {
                status = healthy ? "Healthy" : "Unhealthy",
                checks
            }, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        })
            .WithName("HealthReady")
            .WithTags("Health");

        return app;
    }

    private static async Task<bool> CanConnectAsync(GatewayDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }
}
