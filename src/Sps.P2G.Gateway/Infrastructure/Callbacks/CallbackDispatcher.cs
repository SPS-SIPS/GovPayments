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
        var lockToken = Guid.NewGuid().ToString("N");

        var outboxMessages = await ClaimDueOutboxMessagesAsync(db, lockToken, now, cancellationToken);

        foreach (var outbox in outboxMessages)
        {
            await DispatchOneAsync(db, outbox, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<List<OutboxMessage>> ClaimDueOutboxMessagesAsync(
        GatewayDbContext db,
        string lockToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, options.Value.BatchSize);
        var claimTimeoutSeconds = Math.Max(30, options.Value.ClaimTimeoutSeconds);
        var lockExpiresAt = now.AddSeconds(claimTimeoutSeconds);
        var dueQuery = DueCallbackQuery(db, now);
        var ids = await dueQuery
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        if (db.Database.IsRelational())
        {
            await db.OutboxMessages
                .Where(x => ids.Contains(x.Id))
                .Where(x => x.Type.StartsWith("Callback:"))
                .Where(x =>
                    (x.Status == "Pending" && (x.NextAttemptAt == null || x.NextAttemptAt <= now)) ||
                    (x.Status == "Processing" && x.LockExpiresAt != null && x.LockExpiresAt <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Processing")
                    .SetProperty(x => x.LockToken, lockToken)
                    .SetProperty(x => x.LockedAt, now)
                    .SetProperty(x => x.LockExpiresAt, lockExpiresAt),
                    cancellationToken);

            return await db.OutboxMessages
                .Where(x => x.Status == "Processing" && x.LockToken == lockToken)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(cancellationToken);
        }

        var messages = await db.OutboxMessages
            .Where(x => ids.Contains(x.Id))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var outbox in messages)
        {
            outbox.Status = "Processing";
            outbox.LockToken = lockToken;
            outbox.LockedAt = now;
            outbox.LockExpiresAt = lockExpiresAt;
        }

        await db.SaveChangesAsync(cancellationToken);
        return messages;
    }

    private static IQueryable<OutboxMessage> DueCallbackQuery(GatewayDbContext db, DateTimeOffset now)
    {
        return db.OutboxMessages
            .Where(x => x.Type.StartsWith("Callback:"))
            .Where(x =>
                (x.Status == "Pending" && (x.NextAttemptAt == null || x.NextAttemptAt <= now)) ||
                (x.Status == "Processing" && x.LockExpiresAt != null && x.LockExpiresAt <= now));
    }

    private async Task DispatchOneAsync(GatewayDbContext db, OutboxMessage outbox, CancellationToken cancellationToken)
    {
        var delivery = await db.CallbackDeliveries.FirstOrDefaultAsync(x => x.Id == outbox.AggregateId, cancellationToken);
        if (delivery is null)
        {
            outbox.Status = "DeadLetter";
            outbox.LastError = "Callback delivery record not found.";
            outbox.ProcessedAt = clock.UtcNow;
            ClearClaim(outbox);
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
                ClearClaim(outbox);
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
        ClearClaim(outbox);
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
        ClearClaim(outbox);
    }

    private static void ClearClaim(OutboxMessage outbox)
    {
        outbox.LockToken = "";
        outbox.LockedAt = null;
        outbox.LockExpiresAt = null;
    }
}
