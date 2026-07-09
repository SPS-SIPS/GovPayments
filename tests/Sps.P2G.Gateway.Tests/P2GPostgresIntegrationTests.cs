using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Tests;

public sealed class P2GPostgresIntegrationTests : IClassFixture<PostgresP2GWebApplicationFactory>
{
    private readonly PostgresP2GWebApplicationFactory _factory;

    public P2GPostgresIntegrationTests(PostgresP2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task StartupMigrations_CreatePostgresSchemaAndCoreFlowPersists()
    {
        var client = _factory.CreateClient();
        var setup = await P2GFlowTests.CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: "INV-PG-001",
            upr: "SRA-TAX-2026-INV-PG-001");

        using var verifyResponse = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = setup.Upr,
            accountType = "BILL",
            agent = "BANKBICXXXX",
            verificationId = "VER-PG-001"
        });
        verifyResponse.EnsureSuccessStatusCode();

        using var paymentResponse = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-PG-001",
            endToEndId = setup.Upr,
            amount = 125.00m,
            currency = "USD",
            debtorName = "Sender Name",
            debtorAccount = "9876543210",
            debtorAccountType = "ACCT",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAccountType = "ACCT",
            remittanceInformation = $"BILL:{setup.Upr}",
            msgId = "MSG-PG-001",
            msgDefIdr = "pacs.008"
        });
        paymentResponse.EnsureSuccessStatusCode();
        using var paymentJson = await JsonDocument.ParseAsync(await paymentResponse.Content.ReadAsStreamAsync());
        Assert.Equal("ACCP", paymentJson.RootElement.GetProperty("status").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        Assert.True(db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, db.BillPayments.Count(x => x.TxId == "TX-PG-001"));
        Assert.True(db.BillEvents.Count(x => x.BillId == setup.BillId) >= 4);
    }
}
