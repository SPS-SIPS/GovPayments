using Microsoft.EntityFrameworkCore;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.RateLimiting;
using Sps.P2G.Gateway.Infrastructure.Security;
using Sps.P2G.Gateway.Infrastructure.Time;
using Sps.P2G.Gateway.Modules.Bills;
using Microsoft.Extensions.Options;

namespace Sps.P2G.Gateway.Modules.Dashboard;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dashboard")
            .WithTags("Dashboard")
            .RequireRateLimiting(RateLimitPolicies.Dashboard);
        var keycloakOptions = app.ServiceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        if (keycloakOptions.Enabled)
        {
            group.RequireAuthorization(AuthPolicies.DashboardUser);
        }

        group.MapGet("/bills", async (
            GatewayDbContext db,
            string? upr,
            string? externalInvoiceId,
            string? taxpayerReference,
            string? mdaCode,
            string? serviceCode,
            string? status,
            int page,
            int pageSize,
            CancellationToken cancellationToken) =>
        {
            page = page <= 0 ? 1 : page;
            pageSize = pageSize is <= 0 or > 200 ? 50 : pageSize;
            var query = BillEndpoints.FilterBills(db.Bills, upr, externalInvoiceId, taxpayerReference, mdaCode, serviceCode, status);
            var total = await query.CountAsync(cancellationToken);
            var items = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).Select(x => BillEndpoints.ProjectBill(x)).ToListAsync(cancellationToken);
            return Results.Ok(new { page, pageSize, total, items });
        });

        group.MapGet("/summary", async (GatewayDbContext db, CancellationToken cancellationToken) =>
        {
            var bills = db.Bills;
            var totalBills = await bills.CountAsync(cancellationToken);
            var paidBills = await bills.CountAsync(x => x.StatusProjection == BillStatuses.Paid, cancellationToken);
            var currency = await bills.Select(x => x.Currency).FirstOrDefaultAsync(cancellationToken) ?? "USD";
            return Results.Ok(new
            {
                totalBills,
                availableBills = await bills.CountAsync(x => x.StatusProjection == BillStatuses.Available, cancellationToken),
                fetchedBills = await bills.CountAsync(x => x.StatusProjection == BillStatuses.Fetched, cancellationToken),
                paidBills,
                expiredBills = await bills.CountAsync(x => x.StatusProjection == BillStatuses.Expired, cancellationToken),
                cancelledBills = await bills.CountAsync(x => x.StatusProjection == BillStatuses.Cancelled, cancellationToken),
                voidedBills = await bills.CountAsync(x => x.StatusProjection == BillStatuses.Voided, cancellationToken),
                reversedBills = await bills.CountAsync(x => x.StatusProjection == BillStatuses.Reversed, cancellationToken),
                totalAmountCreated = await bills.SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m,
                totalAmountPaid = await bills.Where(x => x.StatusProjection == BillStatuses.Paid).SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m,
                currency,
                failedCallbacks = await db.CallbackDeliveries.CountAsync(x => x.Status == "Failed" || x.Status == "DeadLetter", cancellationToken),
                pendingCallbacks = await db.OutboxMessages.CountAsync(x => x.Status == "Pending", cancellationToken)
            });
        });

        group.MapGet("/callbacks", async (
            GatewayDbContext db,
            string? status,
            int page,
            int pageSize,
            CancellationToken cancellationToken) =>
        {
            page = page <= 0 ? 1 : page;
            pageSize = pageSize is <= 0 or > 200 ? 50 : pageSize;
            var query = db.CallbackDeliveries.AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(x => x.Status == status);
            }

            var total = await query.CountAsync(cancellationToken);
            var items = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).Select(x => new
            {
                x.Id,
                x.AuthorityId,
                x.EventId,
                x.Url,
                x.Status,
                x.AttemptCount,
                x.LastHttpStatus,
                x.LastError,
                x.NextAttemptAt,
                x.CreatedAt,
                x.UpdatedAt
            }).ToListAsync(cancellationToken);

            return Results.Ok(new { page, pageSize, total, items });
        });

        group.MapGet("/operations", async (
            GatewayDbContext db,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var now = clock.UtcNow;
            var last24Hours = now.AddHours(-24);
            var oldestPendingOutbox = await db.OutboxMessages
                .Where(x => x.Status == "Pending")
                .OrderBy(x => x.CreatedAt)
                .Select(x => (DateTimeOffset?)x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            var rejectReasons = await db.BillPaymentAttempts
                .Where(x => x.Status == "RJCT" && x.CreatedAt >= last24Hours)
                .GroupBy(x => x.Reason)
                .Select(x => new { reason = x.Key, count = x.Count() })
                .OrderByDescending(x => x.count)
                .Take(10)
                .ToListAsync(cancellationToken);

            return Results.Ok(new
            {
                generatedAt = now,
                callbacks = new
                {
                    subscriptionsEnabled = await db.CallbackSubscriptions.CountAsync(x => x.Enabled, cancellationToken),
                    subscriptionsDisabled = await db.CallbackSubscriptions.CountAsync(x => !x.Enabled, cancellationToken),
                    deliveriesPending = await db.CallbackDeliveries.CountAsync(x => x.Status == "Pending", cancellationToken),
                    deliveriesDelivered = await db.CallbackDeliveries.CountAsync(x => x.Status == "Delivered", cancellationToken),
                    deliveriesFailed = await db.CallbackDeliveries.CountAsync(x => x.Status == "Failed", cancellationToken),
                    deliveriesDeadLetter = await db.CallbackDeliveries.CountAsync(x => x.Status == "DeadLetter", cancellationToken)
                },
                outbox = new
                {
                    pending = await db.OutboxMessages.CountAsync(x => x.Status == "Pending", cancellationToken),
                    delivered = await db.OutboxMessages.CountAsync(x => x.Status == "Delivered", cancellationToken),
                    deadLetter = await db.OutboxMessages.CountAsync(x => x.Status == "DeadLetter", cancellationToken),
                    overdue = await db.OutboxMessages.CountAsync(x => x.Status == "Pending" && x.NextAttemptAt <= now, cancellationToken),
                    oldestPendingAgeSeconds = oldestPendingOutbox is null ? 0 : Math.Max(0, (int)(now - oldestPendingOutbox.Value).TotalSeconds)
                },
                payments = new
                {
                    acceptedLast24Hours = await db.BillPayments.CountAsync(x => x.CreatedAt >= last24Hours, cancellationToken),
                    rejectedLast24Hours = await db.BillPaymentAttempts.CountAsync(x => x.Status == "RJCT" && x.CreatedAt >= last24Hours, cancellationToken),
                    reversalsLast24Hours = await db.BillReversals.CountAsync(x => x.CreatedAt >= last24Hours, cancellationToken),
                    topRejectReasonsLast24Hours = rejectReasons
                },
                bills = new
                {
                    createdLast24Hours = await db.Bills.CountAsync(x => x.CreatedAt >= last24Hours, cancellationToken),
                    paidLast24Hours = await db.Bills.CountAsync(x => x.StatusProjection == BillStatuses.Paid && x.UpdatedAt >= last24Hours, cancellationToken),
                    reversedLast24Hours = await db.Bills.CountAsync(x => x.StatusProjection == BillStatuses.Reversed && x.UpdatedAt >= last24Hours, cancellationToken)
                }
            });
        });

        return app;
    }
}
