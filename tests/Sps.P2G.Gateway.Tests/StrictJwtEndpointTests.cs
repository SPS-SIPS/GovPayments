using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sps.P2G.Gateway.Domain.Authorities;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.Security;

namespace Sps.P2G.Gateway.Tests;

public sealed class StrictJwtEndpointTests : IClassFixture<StrictJwtWebApplicationFactory>
{
    private readonly StrictJwtWebApplicationFactory _factory;

    public StrictJwtEndpointTests(StrictJwtWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AuthorityEndpoint_RejectsApiKeyAndAcceptsRevenueAuthorityBearerToken()
    {
        var client = _factory.CreateClient();
        var authority = await SeedAuthorityAsync("SRA");

        var apiKeyRequest = CreateAccountRequest(authority.Id);
        apiKeyRequest.Headers.Add("X-Authority-Api-Key", "legacy-key");
        using var apiKeyResponse = await client.SendAsync(apiKeyRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, apiKeyResponse.StatusCode);

        var bearerRequest = CreateAccountRequest(authority.Id);
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenFactory.Create("SRA", ["p2g-authority"]));
        using var bearerResponse = await client.SendAsync(bearerRequest);
        Assert.Equal(HttpStatusCode.Created, bearerResponse.StatusCode);
    }

    [Fact]
    public async Task SipsConnectEndpoint_RejectsInternalKeyAndAcceptsSipsBearerToken()
    {
        var client = _factory.CreateClient();

        using var internalKeyResponse = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = "missing",
            accountType = "BILL",
            agent = "BANKBICXXXX",
            verificationId = "VER-STRICT-001"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, internalKeyResponse.StatusCode);

        var bearerRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sips-connect/verify")
        {
            Content = JsonContent.Create(new
            {
                accountNo = "missing",
                accountType = "BILL",
                agent = "BANKBICXXXX",
                verificationId = "VER-STRICT-002"
            })
        };
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenFactory.Create("sips-connect-p2g", ["p2g-sips-connect"]));
        using var bearerResponse = await client.SendAsync(bearerRequest);
        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);
    }

    [Fact]
    public async Task SipsConnectEndpoint_RejectsBearerTokenFromUnexpectedClient()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sips-connect/verify")
        {
            Content = JsonContent.Create(new
            {
                accountNo = "missing",
                accountType = "BILL",
                agent = "BANKBICXXXX",
                verificationId = "VER-STRICT-ROGUE"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenFactory.Create("rogue-client", ["p2g-sips-connect"]));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<RevenueAuthority> SeedAuthorityAsync(string code)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var hasher = new Sha256ApiKeyHasher();
        var authority = new RevenueAuthority
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = "Somali Revenue Authority",
            Status = "Active",
            ApiKeyHash = hasher.Hash("legacy-key"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.RevenueAuthorities.Add(authority);
        await db.SaveChangesAsync();
        return authority;
    }

    private static HttpRequestMessage CreateAccountRequest(Guid authorityId)
    {
        return new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{authorityId}/accounts")
        {
            Content = JsonContent.Create(new
            {
                mdaCode = "SRA",
                serviceCode = "TAX",
                revenueCode = "TINCOME",
                bankCode = "010001",
                bankName = "CBS Settlement Bank",
                accountNumber = "1234567890",
                accountName = "Somali Revenue Authority",
                clearingType = "ACCT",
                billerBic = "SIPSBICXXXX",
                currency = "USD",
                isDefault = true
            })
        };
    }
}
