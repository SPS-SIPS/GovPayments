using System.Net;
using System.Text;
using System.Text.Json;

namespace Sps.P2G.Gateway.Tests;

public sealed class RequestProtectionTests
{
    [Fact]
    public async Task OversizedRequestBody_Returns413JsonError()
    {
        await using var factory = new RequestProtectedWebApplicationFactory();
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sips-connect/verify")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { accountNo = new string('x', 128) }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("X-Internal-Api-Key", "test-internal-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("payload_too_large", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(32, json.RootElement.GetProperty("maxRequestBodyBytes").GetInt64());
    }

    [Fact]
    public async Task Responses_IncludeBaselineSecurityHeaders()
    {
        await using var factory = new RequestProtectedWebApplicationFactory();
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
}
