using System.Text.Json;

namespace Sps.P2G.Gateway.Tests;

public sealed class ReconciliationEndpointTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public ReconciliationEndpointTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AuthorityCanListFilteredPaymentsAndReversals()
    {
        var client = _factory.CreateClient();
        var setup = await P2GFlowTests.CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: "INV-RECON-001",
            upr: "SRA-TAX-2026-INV-RECON-001");

        using var paymentResponse = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-RECON-001",
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
            msgId = "MSG-RECON-001",
            msgDefIdr = "pacs.008"
        });
        paymentResponse.EnsureSuccessStatusCode();

        var paymentsRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/payments?txId=TX-RECON-001&upr={setup.Upr}&status=ACSC");
        paymentsRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var paymentsResponse = await client.SendAsync(paymentsRequest);
        paymentsResponse.EnsureSuccessStatusCode();
        using (var paymentsJson = await JsonDocument.ParseAsync(await paymentsResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(1, paymentsJson.RootElement.GetProperty("total").GetInt32());
            var payment = Assert.Single(paymentsJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("TX-RECON-001", payment.GetProperty("txId").GetString());
            Assert.Equal(setup.Upr, payment.GetProperty("upr").GetString());
            Assert.Equal("9876543210", payment.GetProperty("payerAccount").GetString());
            Assert.Equal(125.00m, payment.GetProperty("amount").GetDecimal());
        }

        var paymentsExportRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/payments/export.csv?txId=TX-RECON-001");
        paymentsExportRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var paymentsExportResponse = await client.SendAsync(paymentsExportRequest);
        paymentsExportResponse.EnsureSuccessStatusCode();
        Assert.StartsWith("text/csv", paymentsExportResponse.Content.Headers.ContentType?.MediaType);
        var paymentsCsv = await paymentsExportResponse.Content.ReadAsStringAsync();
        Assert.Contains("paymentId,billId,upr,externalInvoiceId,txId", paymentsCsv);
        Assert.Contains("TX-RECON-001", paymentsCsv);

        using var reversalResponse = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/reverse", new
        {
            txId = "TX-RECON-001",
            endToEndId = setup.Upr,
            reason = "DUPL",
            additionalInfo = "Duplicate payment",
            returnId = "RTN-RECON-001"
        });
        reversalResponse.EnsureSuccessStatusCode();

        var reversalsRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/reversals?txId=RTN-RECON-001&externalInvoiceId=INV-RECON-001");
        reversalsRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var reversalsResponse = await client.SendAsync(reversalsRequest);
        reversalsResponse.EnsureSuccessStatusCode();
        using (var reversalsJson = await JsonDocument.ParseAsync(await reversalsResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(1, reversalsJson.RootElement.GetProperty("total").GetInt32());
            var reversal = Assert.Single(reversalsJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("TX-RECON-001", reversal.GetProperty("originalSipsTransactionId").GetString());
            Assert.Equal("RTN-RECON-001", reversal.GetProperty("reversalTransactionId").GetString());
            Assert.Equal(setup.Upr, reversal.GetProperty("upr").GetString());
            Assert.Equal("DUPL", reversal.GetProperty("reason").GetString());
        }

        var reversalsExportRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/reversals/export.csv?txId=RTN-RECON-001");
        reversalsExportRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var reversalsExportResponse = await client.SendAsync(reversalsExportRequest);
        reversalsExportResponse.EnsureSuccessStatusCode();
        Assert.StartsWith("text/csv", reversalsExportResponse.Content.Headers.ContentType?.MediaType);
        var reversalsCsv = await reversalsExportResponse.Content.ReadAsStringAsync();
        Assert.Contains("reversalId,billId,upr,externalInvoiceId,originalSipsTransactionId", reversalsCsv);
        Assert.Contains("RTN-RECON-001", reversalsCsv);
    }
}
