using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sps.P2G.Gateway.Tests;

public sealed class RevenueConfigurationEndpointTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public RevenueConfigurationEndpointTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AuthorityCanReadConfiguredMdaServiceAccounts()
    {
        var client = _factory.CreateClient();
        var (authorityId, apiKey) = await CreateAuthorityAsync(client);
        using var createResponse = await CreateRevenueAccountAsync(client, authorityId, apiKey, isDefault: true);
        createResponse.EnsureSuccessStatusCode();

        var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{authorityId}/accounts?mdaCode=SRA&serviceCode=TAX");
        listRequest.Headers.Add("X-Authority-Api-Key", apiKey);
        using var listResponse = await client.SendAsync(listRequest);

        listResponse.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await listResponse.Content.ReadAsStreamAsync());
        var account = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("SRA", account.GetProperty("mdaCode").GetString());
        Assert.Equal("Somali Revenue Authority", account.GetProperty("mdaName").GetString());
        Assert.Equal("TAX", account.GetProperty("serviceCode").GetString());
        Assert.Equal("Income Tax", account.GetProperty("serviceName").GetString());
        Assert.True(account.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task DashboardCanUpdateRevenueAccountConfiguration()
    {
        var client = _factory.CreateClient();
        var (authorityId, apiKey) = await CreateAuthorityAsync(client);
        using var createResponse = await CreateRevenueAccountAsync(client, authorityId, apiKey, isDefault: true);
        createResponse.EnsureSuccessStatusCode();
        using var createJson = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
        var accountId = createJson.RootElement.GetProperty("id").GetGuid();

        using var patchResponse = await client.PatchAsJsonAsync($"/api/v1/dashboard/authorities/{authorityId}/revenue-accounts/{accountId}", new
        {
            serviceName = "Business Income Tax",
            status = "Inactive"
        });

        patchResponse.EnsureSuccessStatusCode();
        using var patchJson = await JsonDocument.ParseAsync(await patchResponse.Content.ReadAsStreamAsync());
        Assert.Equal("Business Income Tax", patchJson.RootElement.GetProperty("serviceName").GetString());
        Assert.Equal("Inactive", patchJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task BillCreation_RejectsMdaServiceCurrencyWithoutConfiguredDefaultAccount()
    {
        var client = _factory.CreateClient();
        var (authorityId, apiKey) = await CreateAuthorityAsync(client);
        using var createResponse = await CreateRevenueAccountAsync(client, authorityId, apiKey, isDefault: true, currency: "USD");
        createResponse.EnsureSuccessStatusCode();

        var billRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{authorityId}/bills")
        {
            Content = JsonContent.Create(new
            {
                externalInvoiceId = "INV-MDA-CURRENCY-001",
                upr = "SRA-TAX-2026-INV-MDA-CURRENCY-001",
                mdaCode = "SRA",
                serviceCode = "TAX",
                revenueCode = "TINCOME",
                taxpayerReference = "TIN8891",
                payerDisplayName = "Abdi Mohamed",
                description = "Income Tax Payment",
                amount = 125.00m,
                currency = "SOS",
                dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)),
                expiresAt = DateTimeOffset.UtcNow.AddDays(20)
            })
        };
        billRequest.Headers.Add("X-Authority-Api-Key", apiKey);

        using var billResponse = await client.SendAsync(billRequest);

        Assert.Equal(HttpStatusCode.BadRequest, billResponse.StatusCode);
        using var json = await JsonDocument.ParseAsync(await billResponse.Content.ReadAsStreamAsync());
        Assert.Equal("matching revenue account was not found", json.RootElement.GetProperty("error").GetString());
    }

    private static async Task<(Guid AuthorityId, string ApiKey)> CreateAuthorityAsync(HttpClient client)
    {
        using var authorityResponse = await client.PostAsJsonAsync("/api/v1/authorities", new
        {
            code = $"SRA{Guid.NewGuid():N}"[..12],
            name = "Somali Revenue Authority"
        });
        authorityResponse.EnsureSuccessStatusCode();
        using var authorityJson = await JsonDocument.ParseAsync(await authorityResponse.Content.ReadAsStreamAsync());
        return (
            authorityJson.RootElement.GetProperty("id").GetGuid(),
            authorityJson.RootElement.GetProperty("apiKey").GetString()!);
    }

    private static Task<HttpResponseMessage> CreateRevenueAccountAsync(
        HttpClient client,
        Guid authorityId,
        string apiKey,
        bool isDefault,
        string currency = "USD")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{authorityId}/accounts")
        {
            Content = JsonContent.Create(new
            {
                mdaCode = "SRA",
                mdaName = "Somali Revenue Authority",
                serviceCode = "TAX",
                serviceName = "Income Tax",
                revenueCode = "TINCOME",
                bankCode = "010001",
                bankName = "CBS Settlement Bank",
                accountNumber = "1234567890",
                accountName = "Somali Revenue Authority",
                clearingType = "ACCT",
                billerBic = "SIPSBICXXXX",
                currency,
                isDefault
            })
        };
        request.Headers.Add("X-Authority-Api-Key", apiKey);
        return client.SendAsync(request);
    }
}
