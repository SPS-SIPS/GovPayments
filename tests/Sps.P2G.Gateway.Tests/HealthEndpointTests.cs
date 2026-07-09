using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Live_ReturnsHealthy()
    {
        await using var factory = new HealthWebApplicationFactory();
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Sps.P2G.Gateway", json.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Ready_ReturnsHealthyWhenDatabaseAndBacklogAreHealthy()
    {
        await using var factory = new HealthWebApplicationFactory();
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Healthy", json.RootElement.GetProperty("checks").GetProperty("database").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_ReturnsServiceUnavailableWhenOutboxBacklogExceedsThreshold()
    {
        await using var factory = new HealthWebApplicationFactory(maxPendingOutbox: 0);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "Callback:BillPaid",
                AggregateId = Guid.NewGuid(),
                PayloadJson = "{}",
                Status = "Pending",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("checks").GetProperty("outbox").GetProperty("status").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("checks").GetProperty("outbox").GetProperty("pending").GetInt32());
    }

    private sealed class HealthWebApplicationFactory(int maxPendingOutbox = 10_000) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"health-{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:P2G"] = "Host=unused",
                    ["P2G:DescriptorHmacSecret"] = "test-descriptor-secret",
                    ["P2G:InternalApiKey"] = "test-internal-key",
                    ["Callbacks:Enabled"] = "false",
                    ["Health:MaxPendingOutboxMessages"] = maxPendingOutbox.ToString(),
                    ["Health:MaxDeadLetterCallbacks"] = "100"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<GatewayDbContext>>();
                services.AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            });
        }
    }
}
