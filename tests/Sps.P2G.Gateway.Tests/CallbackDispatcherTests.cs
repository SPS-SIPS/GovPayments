using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.Time;

namespace Sps.P2G.Gateway.Tests;

public sealed class CallbackDispatcherTests
{
    [Fact]
    public async Task DispatchDueCallbacksAsync_DeliversAndSignsSuccessfulCallback()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var provider = BuildProvider(handler);
        var seeded = await SeedCallbackAsync(provider);

        await provider.GetRequiredService<ICallbackDispatcher>().DispatchDueCallbacksAsync(CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var delivery = await db.CallbackDeliveries.SingleAsync();
        var outbox = await db.OutboxMessages.SingleAsync();

        Assert.Equal("Delivered", delivery.Status);
        Assert.Equal("Delivered", outbox.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(1, outbox.Attempts);
        Assert.NotNull(outbox.ProcessedAt);
        Assert.Equal("", outbox.LockToken);
        Assert.Null(outbox.LockedAt);
        Assert.Null(outbox.LockExpiresAt);
        Assert.Single(handler.Requests);

        var request = handler.Requests.Single();
        var timestamp = request.Headers.GetValues("X-SPS-Timestamp").Single();
        var signature = request.Headers.GetValues("X-SPS-Signature").Single();
        var eventId = request.Headers.GetValues("X-SPS-Event-Id").Single();
        var expectedSignature = CallbackSignatureService.Sign(seeded.Secret, timestamp, seeded.EventId, seeded.PayloadJson);

        Assert.Equal(seeded.EventId.ToString(), eventId);
        Assert.Equal(seeded.EventId.ToString(), request.Headers.GetValues("X-SPS-Idempotency-Key").Single());
        Assert.Equal(expectedSignature, signature);
        Assert.Equal(expectedSignature, delivery.Signature);
    }

    [Fact]
    public async Task DispatchDueCallbacksAsync_RetriesServerFailure()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("try later")
        });
        await using var provider = BuildProvider(handler, maxAttempts: 3);
        await SeedCallbackAsync(provider);

        await provider.GetRequiredService<ICallbackDispatcher>().DispatchDueCallbacksAsync(CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var delivery = await db.CallbackDeliveries.SingleAsync();
        var outbox = await db.OutboxMessages.SingleAsync();

        Assert.Equal("Pending", delivery.Status);
        Assert.Equal("Pending", outbox.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(1, outbox.Attempts);
        Assert.Equal(503, delivery.LastHttpStatus);
        Assert.Contains("HTTP 503", delivery.LastError);
        Assert.NotNull(delivery.NextAttemptAt);
        Assert.NotNull(outbox.NextAttemptAt);
        Assert.Equal("", outbox.LockToken);
        Assert.Null(outbox.LockedAt);
        Assert.Null(outbox.LockExpiresAt);
    }

    [Fact]
    public async Task DispatchDueCallbacksAsync_DeadLettersClientFailure()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad payload")
        });
        await using var provider = BuildProvider(handler);
        await SeedCallbackAsync(provider);

        await provider.GetRequiredService<ICallbackDispatcher>().DispatchDueCallbacksAsync(CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var delivery = await db.CallbackDeliveries.SingleAsync();
        var outbox = await db.OutboxMessages.SingleAsync();

        Assert.Equal("DeadLetter", delivery.Status);
        Assert.Equal("DeadLetter", outbox.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(400, delivery.LastHttpStatus);
        Assert.Contains("HTTP 400", delivery.LastError);
        Assert.Null(delivery.NextAttemptAt);
        Assert.NotNull(outbox.ProcessedAt);
    }

    [Fact]
    public async Task DispatchDueCallbacksAsync_DeadLettersUnsafeCallbackUrlWithoutSendingRequest()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var provider = BuildProvider(handler);
        await SeedCallbackAsync(provider, callbackUrl: "https://127.0.0.1/p2g/events");

        await provider.GetRequiredService<ICallbackDispatcher>().DispatchDueCallbacksAsync(CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var delivery = await db.CallbackDeliveries.SingleAsync();
        var outbox = await db.OutboxMessages.SingleAsync();

        Assert.Equal("DeadLetter", delivery.Status);
        Assert.Equal("DeadLetter", outbox.Status);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Contains("private network", delivery.LastError);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DispatchDueCallbacksAsync_SkipsCallbackClaimedByAnotherWorker()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var provider = BuildProvider(handler);
        await SeedCallbackAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var outbox = await db.OutboxMessages.SingleAsync();
            outbox.Status = "Processing";
            outbox.LockToken = "other-worker";
            outbox.LockedAt = TestNow;
            outbox.LockExpiresAt = TestNow.AddMinutes(2);
            await db.SaveChangesAsync();
        }

        await provider.GetRequiredService<ICallbackDispatcher>().DispatchDueCallbacksAsync(CancellationToken.None);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var outbox = await db.OutboxMessages.SingleAsync();
            var delivery = await db.CallbackDeliveries.SingleAsync();
            Assert.Equal("Processing", outbox.Status);
            Assert.Equal("other-worker", outbox.LockToken);
            Assert.Equal(0, outbox.Attempts);
            Assert.Equal(0, delivery.AttemptCount);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task DispatchDueCallbacksAsync_ReclaimsExpiredCallbackClaim()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var provider = BuildProvider(handler);
        await SeedCallbackAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var outbox = await db.OutboxMessages.SingleAsync();
            outbox.Status = "Processing";
            outbox.LockToken = "stale-worker";
            outbox.LockedAt = TestNow.AddMinutes(-5);
            outbox.LockExpiresAt = TestNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        await provider.GetRequiredService<ICallbackDispatcher>().DispatchDueCallbacksAsync(CancellationToken.None);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var outbox = await db.OutboxMessages.SingleAsync();
            var delivery = await db.CallbackDeliveries.SingleAsync();
            Assert.Equal("Delivered", outbox.Status);
            Assert.Equal("Delivered", delivery.Status);
            Assert.Equal("", outbox.LockToken);
            Assert.Null(outbox.LockedAt);
            Assert.Null(outbox.LockExpiresAt);
            Assert.Single(handler.Requests);
        }
    }

    private static ServiceProvider BuildProvider(CapturingHandler handler, int maxAttempts = 3)
    {
        var services = new ServiceCollection();
        var databaseName = $"callbacks-{Guid.NewGuid()}";
        services.AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddDataProtection();
        services.AddSingleton<IClock>(new FixedClock(TestNow));
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
        services.AddSingleton(Options.Create(new CallbackDispatchOptions
        {
            BatchSize = 10,
            MaxAttempts = maxAttempts,
            RequestTimeoutSeconds = 10,
            ClaimTimeoutSeconds = 120
        }));
        services.AddSingleton(Options.Create(new CallbackSecurityOptions()));
        services.AddScoped<ICallbackDispatcher, CallbackDispatcher>();
        return services.BuildServiceProvider();
    }

    private static async Task<SeededCallback> SeedCallbackAsync(ServiceProvider provider, string callbackUrl = "https://example.test/p2g/events")
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var protector = scope.ServiceProvider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Sps.P2G.CallbackSecret.v1");

        var now = TestNow;
        var authorityId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var secret = "callback-secret";
        var payloadJson = $$"""{"eventId":"{{eventId}}","eventType":"BillPaid"}""";

        db.CallbackSubscriptions.Add(new CallbackSubscription
        {
            Id = subscriptionId,
            AuthorityId = authorityId,
            CallbackUrl = callbackUrl,
            Secret = protector.Protect(secret),
            SubscribedEventsJson = """["BillPaid"]""",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.CallbackDeliveries.Add(new CallbackDelivery
        {
            Id = deliveryId,
            AuthorityId = authorityId,
            EventId = eventId,
            CallbackSubscriptionId = subscriptionId,
            Url = callbackUrl,
            PayloadJson = payloadJson,
            Status = "Pending",
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now
        });

        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "Callback:BillPaid",
            AggregateId = deliveryId,
            PayloadJson = payloadJson,
            Status = "Pending",
            CreatedAt = now,
            NextAttemptAt = now
        });

        await db.SaveChangesAsync();
        return new SeededCallback(eventId, payloadJson, secret);
    }

    private sealed record SeededCallback(Guid EventId, string PayloadJson, string Secret);

    private static readonly DateTimeOffset TestNow = new(2026, 7, 8, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            return Task.FromResult(responseFactory(request));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
