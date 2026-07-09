using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sps.P2G.Gateway.Tests;

public sealed class RateLimitingTests
{
    [Fact]
    public async Task AuthorityPolicy_Returns429WhenPermitLimitIsExceeded()
    {
        await using var factory = new RateLimitedWebApplicationFactory();
        var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync("/api/v1/authorities", new
        {
            code = "RLA",
            name = "Rate Limited Authority A"
        });
        first.EnsureSuccessStatusCode();

        using var second = await client.PostAsJsonAsync("/api/v1/authorities", new
        {
            code = "RLB",
            name = "Rate Limited Authority B"
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        using var json = await JsonDocument.ParseAsync(await second.Content.ReadAsStreamAsync());
        Assert.Equal("rate_limit_exceeded", json.RootElement.GetProperty("error").GetString());
    }
}
