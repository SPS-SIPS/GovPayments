using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
    public async Task RevenueConfiguration_RequiresConfigurationAdminForWritesAndAllowsAuthorityRead()
    {
        var client = _factory.CreateClient();
        var authority = await SeedAuthorityAsync("SRA");

        var apiKeyRequest = CreateAccountRequest(authority.Id);
        apiKeyRequest.Headers.Add("X-Authority-Api-Key", "legacy-key");
        using var apiKeyResponse = await client.SendAsync(apiKeyRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, apiKeyResponse.StatusCode);

        var authorityWriteRequest = CreateAccountRequest(authority.Id);
        authorityWriteRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenFactory.Create("SRA", ["p2g-authority"]));
        using var authorityWriteResponse = await client.SendAsync(authorityWriteRequest);
        Assert.Equal(HttpStatusCode.Forbidden, authorityWriteResponse.StatusCode);

        var configAdminRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/dashboard/authorities/{authority.Id}/revenue-accounts")
        {
            Content = CreateAccountRequest(authority.Id).Content
        };
        configAdminRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenFactory.Create("portal-config", ["p2g-configuration-admin"]));
        using var configAdminResponse = await client.SendAsync(configAdminRequest);
        Assert.Equal(HttpStatusCode.Created, configAdminResponse.StatusCode);

        var readRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{authority.Id}/accounts?mdaCode=SRA&serviceCode=TAX");
        readRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenFactory.Create("SRA", ["p2g-authority"]));
        using var readResponse = await client.SendAsync(readRequest);
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
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

    [Fact]
    public async Task DashboardAdmin_CanCreateBillsForAuthority()
    {
        var client = _factory.CreateClient();
        var authority = await SeedAuthorityAsync("SRA");
        await SeedRevenueAccountAsync(authority.Id);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/dashboard/authorities/{authority.Id}/bills")
        {
            Content = JsonContent.Create(new
            {
                externalInvoiceId = "INV-DASH-001",
                upr = "SRA-TAX-2026-INV-DASH-001",
                mdaCode = "SRA",
                serviceCode = "TAX",
                revenueCode = "TINCOME",
                taxpayerReference = "TIN8891",
                payerDisplayName = "Abdi Mohamed",
                description = "Income Tax Payment",
                amount = 125.00m,
                currency = "USD",
                dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)),
                expiresAt = DateTimeOffset.UtcNow.AddDays(20)
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenFactory.Create("p2g-dashboard", ["p2g-dashboard", "p2g-configuration-admin"]));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task DashboardAdmin_CanImportBillsFromCsv()
    {
        var client = _factory.CreateClient();
        var authority = await SeedAuthorityAsync("SRA");
        await SeedRevenueAccountAsync(authority.Id);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(20).ToString("O");
        var csv = string.Join('\n', [
            "externalInvoiceId,upr,mdaCode,serviceCode,revenueCode,taxpayerReference,payerDisplayName,description,amount,currency,dueDate,expiresAt",
            $"INV-CSV-001,SRA-TAX-2026-INV-CSV-001,SRA,TAX,TINCOME,TIN8891,Abdi Mohamed,Income Tax Payment,125.00,USD,{DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)):O},{expiresAt}",
            $"INV-CSV-002,SRA-TAX-2026-INV-CSV-002,SRA,TAX,TINCOME,TIN8892,Amina Ali,\"Quoted, description\",130.50,USD,,{expiresAt}"
        ]);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("text/csv") }
        }, "file", "bills.csv");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/dashboard/authorities/{authority.Id}/bills/import-csv")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTokenFactory.Create("p2g-dashboard", ["p2g-dashboard", "p2g-configuration-admin"]));

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        Assert.Equal(2, db.Bills.Count(x => x.AuthorityId == authority.Id && x.ExternalInvoiceId.StartsWith("INV-CSV-")));
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

    private async Task SeedRevenueAccountAsync(Guid authorityId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.RevenueAccounts.Add(new RevenueAccount
        {
            Id = Guid.NewGuid(),
            AuthorityId = authorityId,
            MdaCode = "SRA",
            MdaName = "Somali Revenue Authority",
            ServiceCode = "TAX",
            ServiceName = "Income Tax",
            RevenueCode = "TINCOME",
            BankCode = "010001",
            BankName = "CBS Settlement Bank",
            AccountNumber = "1234567890",
            AccountName = "Somali Revenue Authority",
            ClearingType = "ACCT",
            BillerBic = "SIPSBICXXXX",
            Currency = "USD",
            IsDefault = true,
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
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
