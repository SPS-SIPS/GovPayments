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
            categoryPurpose = "C2GCRT",
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
        Assert.Equal("ACSC", paymentJson.RootElement.GetProperty("status").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        Assert.True(db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, db.BillPayments.Count(x => x.TxId == "TX-PG-001"));
        Assert.Equal(1, db.BillPaymentAttempts.Count(x => x.TxId == "TX-PG-001" && x.Reason == "PAYMENT_ACCEPTED_PENDING_COMPLETION"));

        await P2GFlowTests.CompletePaymentAsync(client, "TX-PG-001", setup.Upr);
        Assert.Equal(1, db.BillPayments.Count(x => x.TxId == "TX-PG-001"));
        Assert.True(db.BillEvents.Count(x => x.BillId == setup.BillId) >= 4);
    }

    [Fact]
    public async Task ConcurrentDuplicatePaymentConfirm_IsIdempotentUnderPostgresConstraints()
    {
        var client = _factory.CreateClient();
        var setup = await P2GFlowTests.CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: "INV-PG-RACE-001",
            upr: "SRA-TAX-2026-INV-PG-RACE-001");

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
            {
                fromBIC = "BANKBICXXXX",
                toBIC = "SIPSBICXXXX",
                categoryPurpose = "C2GCRT",
                txId = "TX-PG-RACE-001",
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
                msgId = "MSG-PG-RACE-001",
                msgDefIdr = "pacs.008"
            }))
            .ToArray();

        using var responses = new CompositeHttpResponseMessage(await Task.WhenAll(tasks));
        foreach (var response in responses.Items)
        {
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal("ACSC", json.RootElement.GetProperty("status").GetString());
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        Assert.Equal(0, db.BillPayments.Count(x => x.TxId == "TX-PG-RACE-001"));
        Assert.Equal(1, db.BillPaymentAttempts.Count(x => x.TxId == "TX-PG-RACE-001" && x.Reason == "PAYMENT_ACCEPTED_PENDING_COMPLETION"));

        await P2GFlowTests.CompletePaymentAsync(client, "TX-PG-RACE-001", setup.Upr);
        Assert.Equal(1, db.BillPayments.Count(x => x.TxId == "TX-PG-RACE-001"));
        Assert.Equal(1, db.IdempotencyRecords.Count(x => x.Key == "payment:confirm:TX-PG-RACE-001"));
        Assert.Equal(2, db.BillEvents.Count(x => x.BillId == setup.BillId && (x.EventType == "PaymentAccepted" || x.EventType == "BillPaid")));
    }

    private sealed class CompositeHttpResponseMessage(HttpResponseMessage[] items) : IDisposable
    {
        public HttpResponseMessage[] Items { get; } = items;

        public void Dispose()
        {
            foreach (var item in Items)
            {
                item.Dispose();
            }
        }
    }
}
