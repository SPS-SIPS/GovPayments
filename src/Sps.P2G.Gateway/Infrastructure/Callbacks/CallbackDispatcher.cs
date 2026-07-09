using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.Time;

namespace Sps.P2G.Gateway.Infrastructure.Callbacks;

public interface ICallbackDispatcher
{
    Task DispatchDueCallbacksAsync(CancellationToken cancellationToken);
}

public sealed class CallbackDispatcher(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    IClock clock,
    IOptions<CallbackDispatchOptions> options,
    IOptions<CallbackSecurityOptions> securityOptions) : ICallbackDispatcher
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Sps.P2G.CallbackSecret.v1");

    public async Task DispatchDueCallbacksAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var now = clock.UtcNow;

        var outboxMessages = await db.OutboxMessages
            .Where(x => x.Status == "Pending" && x.Type.StartsWith("Callback:") && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt)
            .Take(Math.Max(1, options.Value.BatchSize))
            .ToListAsync(cancellationToken);

        foreach (var outbox in outboxMessages)
        {
            await DispatchOneAsync(db, outbox, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task DispatchOneAsync(GatewayDbContext db, OutboxMessage outbox, CancellationToken cancellationToken)
    {
        var delivery = await db.CallbackDeliveries.FirstOrDefaultAsync(x => x.Id == outbox.AggregateId, cancellationToken);
        if (delivery is null)
        {
            outbox.Status = "DeadLetter";
            outbox.LastError = "Callback delivery record not found.";
            outbox.ProcessedAt = clock.UtcNow;
            return;
        }

        var subscription = await db.CallbackSubscriptions.FirstOrDefaultAsync(x => x.Id == delivery.CallbackSubscriptionId, cancellationToken);
        if (subscription is null || !subscription.Enabled)
        {
            MarkDeadLetter(outbox, delivery, "Callback subscription is missing or disabled.");
            return;
        }

        var callbackUrlValidation = CallbackUrlValidator.Validate(delivery.Url, securityOptions.Value);
        if (!callbackUrlValidation.IsValid)
        {
            MarkDeadLetter(outbox, delivery, callbackUrlValidation.Error!);
            return;
        }

        var now = clock.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var secret = _protector.Unprotect(subscription.Secret);
        var signature = CallbackSignatureService.Sign(secret, timestamp, delivery.EventId, delivery.PayloadJson);

        delivery.AttemptCount += 1;
        delivery.LastAttemptAt = now;
        delivery.Signature = signature;
        delivery.UpdatedAt = now;
        outbox.Attempts += 1;

        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.RequestTimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
        {
            Content = new StringContent(delivery.PayloadJson, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-SPS-Event-Id", delivery.EventId.ToString());
        request.Headers.Add("X-SPS-Timestamp", timestamp);
        request.Headers.Add("X-SPS-Signature", signature);
        request.Headers.Add("X-SPS-Idempotency-Key", delivery.EventId.ToString());

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            delivery.LastHttpStatus = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = "Delivered";
                delivery.LastError = "";
                delivery.NextAttemptAt = null;
                outbox.Status = "Delivered";
                outbox.LastError = "";
                outbox.ProcessedAt = now;
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var error = $"HTTP {(int)response.StatusCode}: {responseBody}";
            if ((int)response.StatusCode >= 500)
            {
                MarkRetryOrDeadLetter(outbox, delivery, error);
                return;
            }

            MarkDeadLetter(outbox, delivery, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            MarkRetryOrDeadLetter(outbox, delivery, ex.Message);
        }
    }

    private void MarkRetryOrDeadLetter(OutboxMessage outbox, CallbackDelivery delivery, string error)
    {
        if (delivery.AttemptCount >= Math.Max(1, options.Value.MaxAttempts))
        {
            MarkDeadLetter(outbox, delivery, error);
            return;
        }

        var delayMinutes = Math.Min(60, Math.Pow(2, Math.Max(0, delivery.AttemptCount - 1)));
        var nextAttempt = clock.UtcNow.AddMinutes(delayMinutes);
        delivery.Status = "Pending";
        delivery.LastError = error;
        delivery.NextAttemptAt = nextAttempt;
        delivery.UpdatedAt = clock.UtcNow;
        outbox.Status = "Pending";
        outbox.LastError = error;
        outbox.NextAttemptAt = nextAttempt;
    }

    private void MarkDeadLetter(OutboxMessage outbox, CallbackDelivery delivery, string error)
    {
        var now = clock.UtcNow;
        delivery.Status = "DeadLetter";
        delivery.LastError = error;
        delivery.NextAttemptAt = null;
        delivery.UpdatedAt = now;
        outbox.Status = "DeadLetter";
        outbox.LastError = error;
        outbox.ProcessedAt = now;
    }
}
