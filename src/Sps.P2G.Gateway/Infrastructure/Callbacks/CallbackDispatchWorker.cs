using Microsoft.Extensions.Options;

namespace Sps.P2G.Gateway.Infrastructure.Callbacks;

public sealed class CallbackDispatchWorker(
    ICallbackDispatcher dispatcher,
    IOptions<CallbackDispatchOptions> options,
    ILogger<CallbackDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Callback dispatch worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.DispatchDueCallbacksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Callback dispatch cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)), stoppingToken);
        }
    }
}
