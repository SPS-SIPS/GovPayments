using Microsoft.Extensions.DependencyInjection;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Domain.Payments;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Tests;

public sealed class MetricsEndpointTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public MetricsEndpointTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Metrics_ReturnsPrometheusTextForCoreOperationalCounts()
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
                ExternalInvoiceId = "INV-METRICS-001",
                UPR = "SRA-TAX-2026-INV-METRICS-001",
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
                CreatedAt = now,
                UpdatedAt = now,
                CreatedPayloadJson = "{}",
                PayloadHash = "payload",
                RecordHash = "record"
            });
            db.BillPayments.Add(new BillPayment
            {
                Id = Guid.NewGuid(),
                BillId = billId,
                AuthorityId = authorityId,
                SipsTransactionId = "TX-METRICS-001",
                TxId = "TX-METRICS-001",
                EndToEndId = "SRA-TAX-2026-INV-METRICS-001",
                Amount = 125m,
                Currency = "USD",
                Status = "ACSC",
                PaidAt = now,
                CreatedAt = now
            });
            db.BillPaymentAttempts.Add(new BillPaymentAttempt
            {
                Id = Guid.NewGuid(),
                AuthorityId = authorityId,
                BillId = billId,
                TxId = "TX-METRICS-RJCT",
                EndToEndId = "SRA-TAX-2026-INV-METRICS-001",
                Status = "RJCT",
                Reason = "AMOUNT_MISMATCH",
                RawPayloadJson = "{}",
                CreatedAt = now
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
                Status = "Pending",
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
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        using var response = await client.GetAsync("/metrics");

        response.EnsureSuccessStatusCode();
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sps_p2g_bills_total{status=\"Paid\"}", body);
        Assert.Contains("sps_p2g_payments_total", body);
        Assert.Contains("sps_p2g_payment_rejections_total{reason=\"AMOUNT_MISMATCH\"}", body);
        Assert.Contains("sps_p2g_callback_deliveries_total{status=\"Pending\"}", body);
        Assert.Contains("sps_p2g_outbox_messages_total{status=\"Pending\"}", body);
    }
}
