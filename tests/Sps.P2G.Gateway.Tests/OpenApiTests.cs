using System.Text.Json;

namespace Sps.P2G.Gateway.Tests;

public sealed class OpenApiTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public OpenApiTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenApiDocument_IncludesCoreP2GEndpoints()
    {
        var client = _factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var paths = json.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/v1/authorities/{authorityId}/bills", out _));
        Assert.True(paths.TryGetProperty("/api/v1/authorities/{authorityId}/bills/{billId}/void", out _));
        Assert.True(paths.TryGetProperty("/api/v1/authorities/{authorityId}/payments", out _));
        Assert.True(paths.TryGetProperty("/api/v1/authorities/{authorityId}/payments/export.csv", out _));
        Assert.True(paths.TryGetProperty("/api/v1/authorities/{authorityId}/reversals", out _));
        Assert.True(paths.TryGetProperty("/api/v1/authorities/{authorityId}/reversals/export.csv", out _));
        Assert.True(paths.TryGetProperty("/api/v1/authorities/{authorityId}/callback-deliveries", out _));
        Assert.True(paths.TryGetProperty("/api/v1/authorities/{authorityId}/callback-deliveries/{deliveryId}/retry", out _));
        Assert.True(paths.TryGetProperty("/api/v1/sips-connect/verify", out _));
        Assert.True(paths.TryGetProperty("/api/v1/sips-connect/payments/confirm", out _));
        Assert.True(paths.TryGetProperty("/api/v1/sips-connect/payments/status", out _));
        Assert.True(paths.TryGetProperty("/api/v1/sips-connect/payments/reverse", out _));
        Assert.True(paths.TryGetProperty("/api/v1/dashboard/operations", out _));
        Assert.True(paths.TryGetProperty("/health/ready", out _));
        Assert.True(paths.TryGetProperty("/metrics", out _));
    }
}
