using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Tests;

public sealed class CallbackAdminEndpointTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public CallbackAdminEndpointTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AuthorityCanListUpdateAndRotateCallbackSubscription()
    {
        var client = _factory.CreateClient();
        var setup = await P2GFlowTests.CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: "INV-CB-ADMIN-001",
            upr: "SRA-TAX-2026-INV-CB-ADMIN-001");
        var (subscriptionId, originalSecret) = await CreateSubscriptionAsync(client, setup);

        var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/callback-subscriptions");
        listRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var listResponse = await client.SendAsync(listRequest);
        listResponse.EnsureSuccessStatusCode();
        using (var listJson = await JsonDocument.ParseAsync(await listResponse.Content.ReadAsStreamAsync()))
        {
            var subscription = Assert.Single(listJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(subscriptionId, subscription.GetProperty("id").GetGuid());
            Assert.True(subscription.GetProperty("enabled").GetBoolean());
        }

        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/authorities/{setup.AuthorityId}/callback-subscriptions/{subscriptionId}")
        {
            Content = JsonContent.Create(new
            {
                enabled = false,
                subscribedEvents = new[] { "BillPaid", "BillReversed" }
            })
        };
        patchRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var patchResponse = await client.SendAsync(patchRequest);
        patchResponse.EnsureSuccessStatusCode();
        using (var patchJson = await JsonDocument.ParseAsync(await patchResponse.Content.ReadAsStreamAsync()))
        {
            Assert.False(patchJson.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Contains(patchJson.RootElement.GetProperty("subscribedEvents").EnumerateArray(), x => x.GetString() == "BillReversed");
        }

        var rotateRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{setup.AuthorityId}/callback-subscriptions/{subscriptionId}/rotate-secret");
        rotateRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var rotateResponse = await client.SendAsync(rotateRequest);
        rotateResponse.EnsureSuccessStatusCode();
        using var rotateJson = await JsonDocument.ParseAsync(await rotateResponse.Content.ReadAsStreamAsync());
        Assert.NotEqual(originalSecret, rotateJson.RootElement.GetProperty("secret").GetString());
    }

    [Fact]
    public async Task AuthorityCanListAndRetryDeadLetterCallbackDelivery()
    {
        var client = _factory.CreateClient();
        var setup = await P2GFlowTests.CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: "INV-CB-ADMIN-002",
            upr: "SRA-TAX-2026-INV-CB-ADMIN-002");
        var (subscriptionId, _) = await CreateSubscriptionAsync(client, setup);
        var deliveryId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.CallbackDeliveries.Add(new CallbackDelivery
            {
                Id = deliveryId,
                AuthorityId = setup.AuthorityId,
                EventId = eventId,
                CallbackSubscriptionId = subscriptionId,
                Url = "https://ra.gov.so/p2g/events",
                PayloadJson = JsonSerializer.Serialize(new { eventId, eventType = "BillPaid", billId = setup.BillId }),
                Signature = "",
                Status = "DeadLetter",
                AttemptCount = 3,
                LastHttpStatus = 400,
                LastError = "HTTP 400",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "Callback:BillPaid",
                AggregateId = deliveryId,
                PayloadJson = "{}",
                Status = "DeadLetter",
                Attempts = 3,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                ProcessedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastError = "HTTP 400"
            });
            await db.SaveChangesAsync();
        }

        var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/callback-deliveries?status=DeadLetter");
        listRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var listResponse = await client.SendAsync(listRequest);
        listResponse.EnsureSuccessStatusCode();
        using (var listJson = await JsonDocument.ParseAsync(await listResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(1, listJson.RootElement.GetProperty("total").GetInt32());
            var delivery = Assert.Single(listJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(deliveryId, delivery.GetProperty("id").GetGuid());
            Assert.Equal("DeadLetter", delivery.GetProperty("status").GetString());
        }

        var retryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{setup.AuthorityId}/callback-deliveries/{deliveryId}/retry");
        retryRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var retryResponse = await client.SendAsync(retryRequest);
        retryResponse.EnsureSuccessStatusCode();
        using (var retryJson = await JsonDocument.ParseAsync(await retryResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("Pending", retryJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, retryJson.RootElement.GetProperty("attemptCount").GetInt32());
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var delivery = db.CallbackDeliveries.Single(x => x.Id == deliveryId);
            var outbox = db.OutboxMessages.Single(x => x.AggregateId == deliveryId);
            Assert.Equal("Pending", delivery.Status);
            Assert.Equal("Pending", outbox.Status);
            Assert.Null(outbox.ProcessedAt);
            Assert.Equal(0, outbox.Attempts);
        }
    }

    [Theory]
    [InlineData("http://ra.gov.so/p2g/events", "HTTPS")]
    [InlineData("https://localhost/p2g/events", "localhost")]
    [InlineData("https://10.0.0.4/p2g/events", "private network")]
    public async Task CreateCallbackSubscription_RejectsUnsafeCallbackUrls(string callbackUrl, string expectedError)
    {
        var client = _factory.CreateClient();
        var setup = await P2GFlowTests.CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: $"INV-CB-URL-{Guid.NewGuid():N}",
            upr: $"SRA-TAX-2026-CB-URL-{Guid.NewGuid():N}");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{setup.AuthorityId}/callback-subscriptions")
        {
            Content = JsonContent.Create(new
            {
                callbackUrl,
                subscribedEvents = new[] { "BillPaid" },
                enabled = true
            })
        };
        request.Headers.Add("X-Authority-Api-Key", setup.ApiKey);

        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Contains(expectedError, json.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(Guid SubscriptionId, string Secret)> CreateSubscriptionAsync(HttpClient client, P2GFlowTests.TestSetup setup)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{setup.AuthorityId}/callback-subscriptions")
        {
            Content = JsonContent.Create(new
            {
                callbackUrl = "https://ra.gov.so/p2g/events",
                subscribedEvents = new[] { "BillPaid" },
                enabled = true
            })
        };
        request.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (json.RootElement.GetProperty("id").GetGuid(), json.RootElement.GetProperty("secret").GetString()!);
    }
}
