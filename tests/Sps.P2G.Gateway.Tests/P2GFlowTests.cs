using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Tests;

public sealed class P2GFlowTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public P2GFlowTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FullP2GFlow_VerifiesBillAndAcceptsExactPayment()
    {
        var client = _factory.CreateClient();
        var setup = await CreateAuthorityAccountAndBillAsync(client);

        using var verifyResponse = await PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = setup.Upr,
            accountType = "BILL",
            agent = "SENDBICXXXX",
            verificationId = "VER-001",
            payerBankCode = "010003",
            payerChannel = "MOBILE_BANKING"
        });

        verifyResponse.EnsureSuccessStatusCode();
        using var verifyJson = await JsonDocument.ParseAsync(await verifyResponse.Content.ReadAsStreamAsync());
        Assert.True(verifyJson.RootElement.GetProperty("isVerified").GetBoolean());
        Assert.StartsWith("P2G1|SRA|TAX|INV123456|USD|125.00|", verifyJson.RootElement.GetProperty("name").GetString());
        Assert.Equal("1234567890", verifyJson.RootElement.GetProperty("accountNo").GetString());

        using var paymentResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "SENDBICXXXX",
            toBIC = "RECVBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-001",
            endToEndId = setup.Upr,
            amount = 125.00m,
            currency = "USD",
            debtorName = "Abdul Ahmed",
            debtorAccount = "408178105111112923438",
            debtorAccountType = "ACCT",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAccountType = "ACCT",
            remittanceInformation = $"BILL:{setup.Upr}",
            msgId = "MSG-001",
            msgDefIdr = "pacs.008"
        });

        paymentResponse.EnsureSuccessStatusCode();
        using var paymentJson = await JsonDocument.ParseAsync(await paymentResponse.Content.ReadAsStreamAsync());
        Assert.Equal("ACCP", paymentJson.RootElement.GetProperty("status").GetString());

        var billRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/bills/{setup.BillId}");
        billRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var billResponse = await client.SendAsync(billRequest);
        billResponse.EnsureSuccessStatusCode();
        using var billJson = await JsonDocument.ParseAsync(await billResponse.Content.ReadAsStreamAsync());
        Assert.Equal("Paid", billJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SipsVerify_WithAcctTypeVerifiesRevenueAccount()
    {
        var client = _factory.CreateClient();
        await CreateAuthorityAccountAndBillAsync(client, invoiceId: "INV-ACCT-VERIFY", upr: "SRA-TAX-2026-INV-ACCT-VERIFY");

        using var verifyResponse = await PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = "1234567890",
            type = "ACCT",
            agent = "SENDBICXXXX",
            verificationId = "VER-ACCT-001"
        });

        verifyResponse.EnsureSuccessStatusCode();
        using var verifyJson = await JsonDocument.ParseAsync(await verifyResponse.Content.ReadAsStreamAsync());
        Assert.True(verifyJson.RootElement.GetProperty("isVerified").GetBoolean());
        Assert.Equal("Account verified", verifyJson.RootElement.GetProperty("message").GetString());
        Assert.Equal("1234567890", verifyJson.RootElement.GetProperty("accountNo").GetString());
        Assert.Equal("ACCT", verifyJson.RootElement.GetProperty("accountType").GetString());
        Assert.Equal("Somali Revenue Authority", verifyJson.RootElement.GetProperty("name").GetString());
        Assert.Equal("USD", verifyJson.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task PaymentConfirm_RejectsAmountMismatchAndLeavesBillFetched()
    {
        var client = _factory.CreateClient();
        var setup = await CreateAuthorityAccountAndBillAsync(client, invoiceId: "INV654321", upr: "SRA-TAX-2026-INV654321");

        using var verifyResponse = await PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = setup.Upr,
            accountType = "BILL",
            agent = "SENDBICXXXX",
            verificationId = "VER-002"
        });
        verifyResponse.EnsureSuccessStatusCode();

        using var paymentResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "SENDBICXXXX",
            toBIC = "RECVBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-002",
            endToEndId = setup.Upr,
            amount = 124.99m,
            currency = "USD",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAccountType = "ACCT",
            remittanceInformation = $"BILL:{setup.Upr}",
            msgDefIdr = "pacs.008"
        });

        paymentResponse.EnsureSuccessStatusCode();
        using var paymentJson = await JsonDocument.ParseAsync(await paymentResponse.Content.ReadAsStreamAsync());
        Assert.Equal("RJCT", paymentJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("AMOUNT_MISMATCH", paymentJson.RootElement.GetProperty("reason").GetString());

        var billRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/bills/{setup.BillId}");
        billRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var billResponse = await client.SendAsync(billRequest);
        billResponse.EnsureSuccessStatusCode();
        using var billJson = await JsonDocument.ParseAsync(await billResponse.Content.ReadAsStreamAsync());
        Assert.Equal("Fetched", billJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task VoidBill_RequiresDescriptionAndBlocksCitizenPayment()
    {
        var client = _factory.CreateClient();
        var setup = await CreateAuthorityAccountAndBillAsync(client, invoiceId: "INV-VOID-001", upr: "SRA-TAX-2026-INV-VOID-001");

        var missingDescriptionRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{setup.AuthorityId}/bills/{setup.BillId}/void")
        {
            Content = JsonContent.Create(new { description = "" })
        };
        missingDescriptionRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var missingDescriptionResponse = await client.SendAsync(missingDescriptionRequest);
        Assert.Equal(HttpStatusCode.BadRequest, missingDescriptionResponse.StatusCode);

        var voidRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{setup.AuthorityId}/bills/{setup.BillId}/void")
        {
            Content = JsonContent.Create(new { description = "Invoice was issued against the wrong taxpayer reference" })
        };
        voidRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var voidResponse = await client.SendAsync(voidRequest);
        voidResponse.EnsureSuccessStatusCode();
        using (var voidJson = await JsonDocument.ParseAsync(await voidResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("Voided", voidJson.RootElement.GetProperty("status").GetString());
        }

        using var verifyResponse = await PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = setup.Upr,
            accountType = "BILL",
            agent = "SENDBICXXXX",
            verificationId = "VER-VOID-001"
        });
        verifyResponse.EnsureSuccessStatusCode();
        using (var verifyJson = await JsonDocument.ParseAsync(await verifyResponse.Content.ReadAsStreamAsync()))
        {
            Assert.False(verifyJson.RootElement.GetProperty("isVerified").GetBoolean());
            Assert.Equal("VOIDED", verifyJson.RootElement.GetProperty("message").GetString());
        }

        using var paymentResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "SENDBICXXXX",
            toBIC = "RECVBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-VOID-001",
            endToEndId = setup.Upr,
            amount = 125.00m,
            currency = "USD",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAccountType = "ACCT",
            remittanceInformation = $"BILL:{setup.Upr}",
            msgDefIdr = "pacs.008"
        });
        paymentResponse.EnsureSuccessStatusCode();
        using (var paymentJson = await JsonDocument.ParseAsync(await paymentResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("RJCT", paymentJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("VOIDED", paymentJson.RootElement.GetProperty("reason").GetString());
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var voidEvent = Assert.Single(db.BillEvents, x => x.BillId == setup.BillId && x.EventType == BillEventTypes.BillVoided);
        Assert.Contains("wrong taxpayer reference", voidEvent.PayloadJson);
    }

    [Fact]
    public async Task SipsConnectEndpoints_AcceptAndReturnAdapterShapedPayloads()
    {
        var client = _factory.CreateClient();
        var setup = await CreateAuthorityAccountAndBillAsync(client, invoiceId: "INV777001", upr: "SRA-TAX-2026-INV777001");

        using var verifyResponse = await PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = setup.Upr,
            accountType = "BILL",
            agent = "BANKBICXXXX",
            verificationId = "VER-ADAPTER-001"
        });
        verifyResponse.EnsureSuccessStatusCode();
        using (var verifyJson = await JsonDocument.ParseAsync(await verifyResponse.Content.ReadAsStreamAsync()))
        {
            Assert.True(verifyJson.RootElement.GetProperty("isVerified").GetBoolean());
            Assert.Equal("Invoice verified", verifyJson.RootElement.GetProperty("message").GetString());
            Assert.Equal("1234567890", verifyJson.RootElement.GetProperty("accountNo").GetString());
            Assert.Equal("ACCT", verifyJson.RootElement.GetProperty("accountType").GetString());
            Assert.StartsWith("P2G1|", verifyJson.RootElement.GetProperty("name").GetString());
            Assert.Equal("Somali Revenue Authority", verifyJson.RootElement.GetProperty("address").GetString());
            Assert.Equal("USD", verifyJson.RootElement.GetProperty("currency").GetString());
        }

        using var paymentResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            localInstrument = "INST",
            categoryPurpose = "C2BBPT",
            txId = "TX-ADAPTER-001",
            endToEndId = setup.Upr,
            amount = 125.00m,
            currency = "USD",
            debtorName = "Sender Name",
            debtorAccount = "9876543210",
            debtorAddress = "Sender Address",
            debtorAccountType = "ACCT",
            debtorAgentBIC = "BANKBICXXXX",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAddress = "Receiver Address",
            creditorAccountType = "ACCT",
            creditorAgentBIC = "SIPSBICXXXX",
            creditorIssuer = "SRA",
            remittanceInformation = $"BILL:{setup.Upr}",
            date = DateTimeOffset.UtcNow,
            settlementMethod = "CLRG",
            chargeBearer = "SLEV",
            bizMsgIdr = "BIZ-ADAPTER-001",
            msgDefIdr = "pacs.008",
            clearingSystem = "SIPS",
            msgId = "MSG-ADAPTER-001"
        });
        paymentResponse.EnsureSuccessStatusCode();
        using (var paymentJson = await JsonDocument.ParseAsync(await paymentResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(setup.Upr, paymentJson.RootElement.GetProperty("endToEndId").GetString());
            Assert.Equal("ACCP", paymentJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("Accepted", paymentJson.RootElement.GetProperty("reason").GetString());
            Assert.Equal("Invoice payment accepted", paymentJson.RootElement.GetProperty("additionalInfo").GetString());
            Assert.True(paymentJson.RootElement.TryGetProperty("acceptanceDate", out _));
            Assert.True(paymentJson.RootElement.TryGetProperty("acceptedAtUtc", out _));
            Assert.Equal("TX-ADAPTER-001", paymentJson.RootElement.GetProperty("txId").GetString());
        }

        using var statusResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/status", new
        {
            endToEndId = setup.Upr,
            txId = "TX-ADAPTER-001",
            agent = "BANKBICXXXX"
        });
        statusResponse.EnsureSuccessStatusCode();
        using (var statusJson = await JsonDocument.ParseAsync(await statusResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("BANKBICXXXX", statusJson.RootElement.GetProperty("fromBIC").GetString());
            Assert.Equal("SIPSBICXXXX", statusJson.RootElement.GetProperty("toBIC").GetString());
            Assert.Equal("C2BBPT", statusJson.RootElement.GetProperty("categoryPurpose").GetString());
            Assert.Equal("TX-ADAPTER-001", statusJson.RootElement.GetProperty("txId").GetString());
            Assert.Equal(setup.Upr, statusJson.RootElement.GetProperty("endToEndId").GetString());
            Assert.Equal("ACSC", statusJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("MSG-ADAPTER-001", statusJson.RootElement.GetProperty("msgId").GetString());
        }

        using var returnResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/reverse", new
        {
            txId = "TX-ADAPTER-001",
            endToEndId = setup.Upr,
            agent = "BANKBICXXXX",
            reason = "DUPL",
            additionalInfo = "Duplicate payment",
            returnId = "RTN-ADAPTER-001"
        });
        returnResponse.EnsureSuccessStatusCode();
        using (var returnJson = await JsonDocument.ParseAsync(await returnResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("TX-ADAPTER-001", returnJson.RootElement.GetProperty("txId").GetString());
            Assert.Equal(setup.Upr, returnJson.RootElement.GetProperty("endToEndId").GetString());
            Assert.Equal("ACCP", returnJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("Accepted", returnJson.RootElement.GetProperty("reason").GetString());
            Assert.Equal("Return accepted for processing", returnJson.RootElement.GetProperty("additionalInfo").GetString());
        }
    }

    [Fact]
    public async Task BillEventsPolling_ReturnsOnlyEventsAfterCursor()
    {
        var client = _factory.CreateClient();
        var setup = await CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: "INV888001",
            upr: "SRA-TAX-2026-INV888001",
            billCorrelationId: "events-corr-001");

        var firstPoll = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/bill-events?cursor=0");
        firstPoll.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var firstResponse = await client.SendAsync(firstPoll);
        firstResponse.EnsureSuccessStatusCode();
        using var firstJson = await JsonDocument.ParseAsync(await firstResponse.Content.ReadAsStreamAsync());
        Assert.True(firstJson.RootElement.GetProperty("hasNewEvents").GetBoolean());
        var nextCursor = firstJson.RootElement.GetProperty("nextCursor").GetString();
        var createdEvent = Assert.Single(firstJson.RootElement.GetProperty("events").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "BillCreated");
        Assert.Equal(setup.BillId, createdEvent.GetProperty("billId").GetGuid());
        Assert.Equal("Authority", createdEvent.GetProperty("actorType").GetString());
        Assert.Equal("RevenueAuthorityApi", createdEvent.GetProperty("sourceSystem").GetString());
        Assert.Equal("events-corr-001", createdEvent.GetProperty("correlationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(createdEvent.GetProperty("payloadHash").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(createdEvent.GetProperty("previousEventHash").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(createdEvent.GetProperty("eventHash").GetString()));
        Assert.Equal(JsonValueKind.Null, createdEvent.GetProperty("payloadJson").ValueKind);

        var payloadPoll = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/bill-events?cursor=0&types=BillCreated&billId={setup.BillId}&includePayload=true");
        payloadPoll.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var payloadResponse = await client.SendAsync(payloadPoll);
        payloadResponse.EnsureSuccessStatusCode();
        using (var payloadJson = await JsonDocument.ParseAsync(await payloadResponse.Content.ReadAsStreamAsync()))
        {
            var payloadEvent = Assert.Single(payloadJson.RootElement.GetProperty("events").EnumerateArray());
            Assert.Equal("BillCreated", payloadEvent.GetProperty("eventType").GetString());
            Assert.Contains("INV888001", payloadEvent.GetProperty("payloadJson").GetString());
        }

        var secondPoll = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorities/{setup.AuthorityId}/bill-events?cursor={nextCursor}");
        secondPoll.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var secondResponse = await client.SendAsync(secondPoll);
        secondResponse.EnsureSuccessStatusCode();
        using var secondJson = await JsonDocument.ParseAsync(await secondResponse.Content.ReadAsStreamAsync());
        Assert.False(secondJson.RootElement.GetProperty("hasNewEvents").GetBoolean());
        Assert.Equal(nextCursor, secondJson.RootElement.GetProperty("nextCursor").GetString());
        Assert.Empty(secondJson.RootElement.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task CallbackSubscription_QueuesOnlySubscribedPaymentEvents()
    {
        var client = _factory.CreateClient();
        var setup = await CreateAuthorityAccountAndBillAsync(client, invoiceId: "INV999001", upr: "SRA-TAX-2026-INV999001");

        var subscriptionRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{setup.AuthorityId}/callback-subscriptions")
        {
            Content = JsonContent.Create(new
            {
                callbackUrl = "https://ra.gov.so/p2g/events",
                subscribedEvents = new[] { "BillPaid" },
                enabled = true
            })
        };
        subscriptionRequest.Headers.Add("X-Authority-Api-Key", setup.ApiKey);
        using var subscriptionResponse = await client.SendAsync(subscriptionRequest);
        subscriptionResponse.EnsureSuccessStatusCode();

        using var verifyResponse = await PostInternalAsync(client, "/api/v1/sips-connect/verify", new
        {
            accountNo = setup.Upr,
            accountType = "BILL",
            agent = "BANKBICXXXX",
            verificationId = "VER-CALLBACK-001"
        });
        verifyResponse.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            Assert.Empty(db.CallbackDeliveries);
            Assert.Empty(db.OutboxMessages);
        }

        using var paymentResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-CALLBACK-001",
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
            msgId = "MSG-CALLBACK-001",
            msgDefIdr = "pacs.008"
        });
        paymentResponse.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var delivery = Assert.Single(db.CallbackDeliveries);
            Assert.Equal("Pending", delivery.Status);
            Assert.Contains("\"eventType\":\"BillPaid\"", delivery.PayloadJson);

            var outbox = Assert.Single(db.OutboxMessages);
            Assert.Equal("Callback:BillPaid", outbox.Type);
            Assert.Equal(delivery.Id, outbox.AggregateId);
        }
    }

    [Fact]
    public async Task DuplicatePayment_IsIdempotentButDifferentPayloadIsRejected()
    {
        var client = _factory.CreateClient();
        var setup = await CreateAuthorityAccountAndBillAsync(client, invoiceId: "INV-IDEMP-001", upr: "SRA-TAX-2026-INV-IDEMP-001");

        var paymentPayload = new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-IDEMP-001",
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
            msgId = "MSG-IDEMP-001",
            msgDefIdr = "pacs.008"
        };

        using var firstResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", paymentPayload);
        firstResponse.EnsureSuccessStatusCode();
        using var secondResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", paymentPayload);
        secondResponse.EnsureSuccessStatusCode();
        using (var secondJson = await JsonDocument.ParseAsync(await secondResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("ACCP", secondJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("Duplicate payment already processed", secondJson.RootElement.GetProperty("additionalInfo").GetString());
        }

        using var differentPayloadResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-IDEMP-001",
            endToEndId = setup.Upr,
            amount = 126.00m,
            currency = "USD",
            creditorName = "Somali Revenue Authority",
            creditorAccount = "1234567890",
            creditorAccountType = "ACCT",
            remittanceInformation = $"BILL:{setup.Upr}",
            msgDefIdr = "pacs.008"
        });
        differentPayloadResponse.EnsureSuccessStatusCode();
        using (var differentJson = await JsonDocument.ParseAsync(await differentPayloadResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("RJCT", differentJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("DUPLICATE_TX_DIFFERENT_PAYLOAD", differentJson.RootElement.GetProperty("reason").GetString());
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        Assert.Equal(1, db.BillPayments.Count(x => x.TxId == "TX-IDEMP-001"));
        Assert.Equal(1, db.IdempotencyRecords.Count(x => x.Key == "payment:confirm:TX-IDEMP-001"));
    }

    [Fact]
    public async Task Reversal_IsIdempotentAndRejectsUnknownOrPartialReversal()
    {
        var client = _factory.CreateClient();
        var setup = await CreateAuthorityAccountAndBillAsync(client, invoiceId: "INV-REV-001", upr: "SRA-TAX-2026-INV-REV-001");

        using var paymentResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/confirm", new
        {
            fromBIC = "BANKBICXXXX",
            toBIC = "SIPSBICXXXX",
            categoryPurpose = "C2BBPT",
            txId = "TX-REV-001",
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
            msgId = "MSG-REV-001",
            msgDefIdr = "pacs.008"
        });
        paymentResponse.EnsureSuccessStatusCode();

        using var unknownResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/reverse", new
        {
            txId = "TX-REV-MISSING",
            endToEndId = setup.Upr,
            reason = "DUPL",
            returnId = "RTN-MISSING"
        });
        unknownResponse.EnsureSuccessStatusCode();
        using (var unknownJson = await JsonDocument.ParseAsync(await unknownResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("RJCT", unknownJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("ORIGINAL_PAYMENT_NOT_FOUND", unknownJson.RootElement.GetProperty("reason").GetString());
        }

        using var partialResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/reverse", new
        {
            txId = "TX-REV-001",
            endToEndId = setup.Upr,
            reason = "DUPL",
            returnId = "RTN-PARTIAL",
            amount = 10.00m,
            currency = "USD"
        });
        partialResponse.EnsureSuccessStatusCode();
        using (var partialJson = await JsonDocument.ParseAsync(await partialResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("RJCT", partialJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("PARTIAL_REVERSAL_NOT_SUPPORTED", partialJson.RootElement.GetProperty("reason").GetString());
        }

        var reversalPayload = new
        {
            txId = "TX-REV-001",
            endToEndId = setup.Upr,
            reason = "DUPL",
            additionalInfo = "Duplicate payment",
            returnId = "RTN-REV-001"
        };
        using var firstReversalResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/reverse", reversalPayload);
        firstReversalResponse.EnsureSuccessStatusCode();
        using var duplicateReversalResponse = await PostInternalAsync(client, "/api/v1/sips-connect/payments/reverse", reversalPayload);
        duplicateReversalResponse.EnsureSuccessStatusCode();
        using (var duplicateJson = await JsonDocument.ParseAsync(await duplicateReversalResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("ACCP", duplicateJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("Duplicate return already processed", duplicateJson.RootElement.GetProperty("additionalInfo").GetString());
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        Assert.Equal(1, db.BillReversals.Count(x => x.OriginalSipsTransactionId == "TX-REV-001" && x.ReversalTransactionId == "RTN-REV-001"));
        Assert.Equal(1, db.IdempotencyRecords.Count(x => x.Key == $"payment:reverse:TX-REV-001:{setup.Upr}:RTN-REV-001"));
    }

    internal static async Task<TestSetup> CreateAuthorityAccountAndBillAsync(
        HttpClient client,
        string invoiceId = "INV123456",
        string upr = "SRA-TAX-2026-INV123456",
        string? billCorrelationId = null)
    {
        using var authorityResponse = await client.PostAsJsonAsync("/api/v1/authorities", new
        {
            code = $"SRA{Guid.NewGuid():N}"[..12],
            name = "Somali Revenue Authority"
        });
        authorityResponse.EnsureSuccessStatusCode();
        using var authorityJson = await JsonDocument.ParseAsync(await authorityResponse.Content.ReadAsStreamAsync());
        var authorityId = authorityJson.RootElement.GetProperty("id").GetGuid();
        var apiKey = authorityJson.RootElement.GetProperty("apiKey").GetString()!;

        var accountRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{authorityId}/accounts")
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
                currency = "USD",
                isDefault = true
            })
        };
        accountRequest.Headers.Add("X-Authority-Api-Key", apiKey);
        using var accountResponse = await client.SendAsync(accountRequest);
        accountResponse.EnsureSuccessStatusCode();

        var billRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/authorities/{authorityId}/bills")
        {
            Content = JsonContent.Create(new
            {
                externalInvoiceId = invoiceId,
                upr,
                mdaCode = "SRA",
                serviceCode = "TAX",
                revenueCode = "TINCOME",
                taxpayerReference = "TIN8891",
                payerDisplayName = "Abdi Mohamed",
                description = "Income Tax Payment",
                amount = 125.00m,
                currency = "USD",
                dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)),
                expiresAt = DateTimeOffset.UtcNow.AddDays(20),
                metadata = new { period = "2026-Q2" }
            })
        };
        billRequest.Headers.Add("X-Authority-Api-Key", apiKey);
        if (!string.IsNullOrWhiteSpace(billCorrelationId))
        {
            billRequest.Headers.Add("X-Correlation-Id", billCorrelationId);
        }

        using var billResponse = await client.SendAsync(billRequest);
        Assert.Equal(HttpStatusCode.Created, billResponse.StatusCode);
        using var billJson = await JsonDocument.ParseAsync(await billResponse.Content.ReadAsStreamAsync());

        return new TestSetup(authorityId, apiKey, billJson.RootElement.GetProperty("billId").GetGuid(), upr);
    }

    internal static Task<HttpResponseMessage> PostInternalAsync(HttpClient client, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Internal-Api-Key", "test-internal-key");
        return client.SendAsync(request);
    }

    internal sealed record TestSetup(Guid AuthorityId, string ApiKey, Guid BillId, string Upr);
}
