using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Domain.Payments;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Tests;

public sealed class DashboardOperationsTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public DashboardOperationsTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Operations_ReturnsCallbackQueueAndPaymentHealthSummary()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var authorityId = Guid.NewGuid();
            var billId = Guid.NewGuid();

            db.Bills.Add(new Bill
            {
                Id = billId,
                AuthorityId = authorityId,
                ExternalInvoiceId = "INV-OPS-001",
                UPR = "SRA-TAX-2026-INV-OPS-001",
                MdaCode = "SRA",
                ServiceCode = "TAX",
                RevenueCode = "TINCOME",
                TaxpayerReference = "TIN8891",
                PayerDisplayName = "Abdi Mohamed",
                Description = "Income Tax Payment",
                Amount = 125m,
                Currency = "USD",
                ExpiresAt = now.AddDays(1),
                StatusProjection = BillStatuses.Paid,
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddHours(-1),
                CreatedPayloadJson = "{}",
                PayloadHash = "payload",
                RecordHash = "record"
            });
            db.CallbackSubscriptions.Add(new CallbackSubscription
            {
                Id = Guid.NewGuid(),
                AuthorityId = authorityId,
                CallbackUrl = "https://ra.gov.so/p2g/events",
                Secret = "protected",
                SubscribedEventsJson = "[]",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CallbackDeliveries.Add(new CallbackDelivery
            {
                Id = Guid.NewGuid(),
                AuthorityId = authorityId,
                EventId = Guid.NewGuid(),
                CallbackSubscriptionId = Guid.NewGuid(),
                Url = "https://ra.gov.so/p2g/events",
                PayloadJson = "{}",
                Signature = "",
                Status = "DeadLetter",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "Callback:BillPaid",
                AggregateId = Guid.NewGuid(),
                PayloadJson = "{}",
                Status = "Pending",
                NextAttemptAt = now.AddMinutes(-10),
                CreatedAt = now.AddMinutes(-30)
            });
            db.BillPayments.Add(new BillPayment
            {
                Id = Guid.NewGuid(),
                BillId = billId,
                AuthorityId = authorityId,
                SipsTransactionId = "TX-OPS-001",
                TxId = "TX-OPS-001",
                EndToEndId = "SRA-TAX-2026-INV-OPS-001",
                Amount = 125m,
                Currency = "USD",
                Status = "ACSC",
                PaidAt = now.AddHours(-1),
                CreatedAt = now.AddHours(-1)
            });
            db.BillPaymentAttempts.Add(new BillPaymentAttempt
            {
                Id = Guid.NewGuid(),
                BillId = billId,
                AuthorityId = authorityId,
                TxId = "TX-OPS-REJ",
                EndToEndId = "SRA-TAX-2026-INV-OPS-001",
                Status = "RJCT",
                Reason = "AMOUNT_MISMATCH",
                RawPayloadJson = "{}",
                CreatedAt = now.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/dashboard/operations");

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(1, json.RootElement.GetProperty("callbacks").GetProperty("subscriptionsEnabled").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("callbacks").GetProperty("deliveriesDeadLetter").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("outbox").GetProperty("pending").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("outbox").GetProperty("overdue").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("payments").GetProperty("acceptedLast24Hours").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("payments").GetProperty("rejectedLast24Hours").GetInt32());
        var reason = Assert.Single(json.RootElement.GetProperty("payments").GetProperty("topRejectReasonsLast24Hours").EnumerateArray());
        Assert.Equal("AMOUNT_MISMATCH", reason.GetProperty("reason").GetString());
        Assert.Equal(1, reason.GetProperty("count").GetInt32());
    }
}
