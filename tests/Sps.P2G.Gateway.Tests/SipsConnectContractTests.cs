using System.Text.Json;

namespace Sps.P2G.Gateway.Tests;

public sealed class SipsConnectContractTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public SipsConnectContractTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Verify_MissingInvoiceReference_ReturnsRejectedVerificationPayload()
    {
        var client = _factory.CreateClient();

        using var response = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            agent = "BANKBICXXXX",
            verificationId = "VER-MISSING-ACCOUNT"
        });

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.False(json.RootElement.GetProperty("isVerified").GetBoolean());
        Assert.Equal("MISSING_ACCOUNT_NO", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task VerifyInvoice_ReturnsLegacyDescriptorInNameForSipsTransport()
    {
        var client = _factory.CreateClient();
        var setup = await P2GFlowTests.CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: "INV-LEGACY-VERIFY",
            upr: "SRA-TAX-2026-INV-LEGACY-VERIFY");

        using var response = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = setup.Upr,
            accountType = "BILL",
            agent = "BANKBICXXXX",
            verificationId = "VER-LEGACY-VERIFY"
        });

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(json.RootElement.GetProperty("isVerified").GetBoolean());
        Assert.StartsWith("P2G1|SRA|TAX|INV-LEGACY-VERIFY|USD|125.00|", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("1234567890", json.RootElement.GetProperty("accountNo").GetString());
        Assert.Equal("ACCT", json.RootElement.GetProperty("accountType").GetString());
        Assert.Equal("Somali Revenue Authority", json.RootElement.GetProperty("address").GetString());
        Assert.Equal("USD", json.RootElement.GetProperty("currency").GetString());
        Assert.False(json.RootElement.TryGetProperty("creditorName", out _));
        Assert.False(json.RootElement.TryGetProperty("billReference", out _));
        Assert.False(json.RootElement.TryGetProperty("amountPayable", out _));
    }

    [Fact]
    public async Task PaymentConfirm_MissingMsgDefIdr_ReturnsRejectedAdapterPayload()
    {
        var client = _factory.CreateClient();

        using var response = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose = "C2GCRT",
            txId = "TX-MISSING-MSGDEF",
            endToEndId = "SRA-TAX-2026-MISSING",
            amount = 125.00m,
            currency = "USD",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAccountType = "ACCT",
            remittanceInformation = "BILL:SRA-TAX-2026-MISSING"
        });

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RJCT", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("MISSING_MSG_DEF_IDR", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("TX-MISSING-MSGDEF", json.RootElement.GetProperty("txId").GetString());
    }

    [Theory]
    [InlineData("C2GCRT")]
    [InlineData("C2GSQR")]
    [InlineData("C2GDQR")]
    [InlineData("B2GCRT")]
    [InlineData("B2GSQR")]
    [InlineData("B2GDQR")]
    public async Task PaymentConfirm_AcceptsGovernmentPaymentCategoryPurposes(string categoryPurpose)
    {
        var client = _factory.CreateClient();

        using var response = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose,
            txId = $"TX-{categoryPurpose}",
            endToEndId = $"SRA-TAX-2026-{categoryPurpose}",
            amount = 125.00m,
            currency = "USD",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAccountType = "ACCT",
            remittanceInformation = $"BILL:SRA-TAX-2026-{categoryPurpose}"
        });

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RJCT", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("MISSING_MSG_DEF_IDR", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal($"TX-{categoryPurpose}", json.RootElement.GetProperty("txId").GetString());
    }

    [Fact]
    public async Task PaymentConfirm_InvalidCategoryPurpose_ReturnsRejectedAdapterPayload()
    {
        var client = _factory.CreateClient();

        using var response = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose = "CASH",
            txId = "TX-BAD-CATEGORY",
            endToEndId = "SRA-TAX-2026-BAD-CATEGORY",
            amount = 125.00m,
            currency = "USD",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAccountType = "ACCT",
            remittanceInformation = "BILL:SRA-TAX-2026-BAD-CATEGORY",
            msgDefIdr = "pacs.008"
        });

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RJCT", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("INVALID_CATEGORY_PURPOSE", json.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Return_MissingReturnId_ReturnsRejectedAdapterPayload()
    {
        var client = _factory.CreateClient();

        using var response = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/reverse", new
        {
            txId = "TX-RETURN-MISSING-ID",
            endToEndId = "SRA-TAX-2026-RETURN",
            reason = "DUPL"
        });

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RJCT", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("MISSING_RETURN_ID", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("TX-RETURN-MISSING-ID", json.RootElement.GetProperty("txId").GetString());
    }

    [Fact]
    public async Task Status_MissingPaymentReference_ReturnsRejectedStatusPayload()
    {
        var client = _factory.CreateClient();

        using var response = await P2GFlowTests.PostInternalAsync(client, "/api/v1/sips-connect/payments/status", new
        {
            agent = "BANKBICXXXX"
        });

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("RJCT", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("MISSING_PAYMENT_REFERENCE", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("txId or endToEndId is required", json.RootElement.GetProperty("additionalInfo").GetString());
    }
}
