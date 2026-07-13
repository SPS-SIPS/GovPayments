using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
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

        group.MapPost("/authorities/{authorityId:guid}/bills", async (
            Guid authorityId,
            CreateBillRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IHashChainService hashChain,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await db.RevenueAuthorities.FirstOrDefaultAsync(x => x.Id == authorityId && x.Status == "Active", cancellationToken);
            if (authority is null)
            {
                return Results.NotFound(new { error = "authority was not found" });
            }

            var result = await BillEndpoints.CreateBillAsync(authority, request, db, hashChain, clock, httpContext.TraceIdentifier, cancellationToken);
            return result.ToHttpResult();
        })
        .RequireAuthorization(AuthPolicies.ConfigurationAdmin);

        group.MapPost("/authorities/{authorityId:guid}/bills/bulk", async (
            Guid authorityId,
            CreateBillRequest[] requests,
            HttpContext httpContext,
            GatewayDbContext db,
            IHashChainService hashChain,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await db.RevenueAuthorities.FirstOrDefaultAsync(x => x.Id == authorityId && x.Status == "Active", cancellationToken);
            if (authority is null)
            {
                return Results.NotFound(new { error = "authority was not found" });
            }

            return Results.Ok(await CreateBillsFromDashboardAsync(authority, requests, db, hashChain, clock, httpContext.TraceIdentifier, cancellationToken));
        })
        .RequireAuthorization(AuthPolicies.ConfigurationAdmin);

        group.MapPost("/authorities/{authorityId:guid}/bills/import-csv", async (
            Guid authorityId,
            IFormFile file,
            HttpContext httpContext,
            GatewayDbContext db,
            IHashChainService hashChain,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await db.RevenueAuthorities.FirstOrDefaultAsync(x => x.Id == authorityId && x.Status == "Active", cancellationToken);
            if (authority is null)
            {
                return Results.NotFound(new { error = "authority was not found" });
            }

            if (file.Length == 0)
            {
                return Results.BadRequest(new { error = "CSV file is empty" });
            }

            var parseResult = await ParseBillCsvAsync(file, cancellationToken);
            if (parseResult.Errors.Count > 0)
            {
                return Results.BadRequest(new { errors = parseResult.Errors });
            }

            return Results.Ok(await CreateBillsFromDashboardAsync(authority, parseResult.Bills, db, hashChain, clock, httpContext.TraceIdentifier, cancellationToken));
        })
        .RequireAuthorization(AuthPolicies.ConfigurationAdmin)
        .DisableAntiforgery();

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

    private static async Task<object> CreateBillsFromDashboardAsync(
        Domain.Authorities.RevenueAuthority authority,
        IReadOnlyList<CreateBillRequest> requests,
        GatewayDbContext db,
        IHashChainService hashChain,
        IClock clock,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var results = new List<object>();
        var created = 0;
        var idempotent = 0;
        var invalid = 0;
        var conflict = 0;

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var result = await BillEndpoints.CreateBillAsync(authority, request, db, hashChain, clock, correlationId, cancellationToken);
            if (result.Status == "created") created++;
            else if (result.Status == "idempotent") idempotent++;
            else if (result.Status == "invalid") invalid++;
            else if (result.Status == "conflict") conflict++;

            results.Add(new
            {
                row = index + 1,
                request.ExternalInvoiceId,
                request.Upr,
                status = result.Status,
                result.BillId,
                result.Error
            });
        }

        return new
        {
            total = requests.Count,
            created,
            idempotent,
            invalid,
            conflict,
            results
        };
    }

    private static async Task<BillCsvParseResult> ParseBillCsvAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return new BillCsvParseResult([], ["CSV header row is required"]);
        }

        string[] headers;
        try
        {
            headers = ParseCsvLine(headerLine).Select(x => x.Trim()).ToArray();
        }
        catch (FormatException ex)
        {
            return new BillCsvParseResult([], [$"header: {ex.Message}"]);
        }
        var headerMap = headers
            .Select((name, index) => new { name, index })
            .Where(x => !string.IsNullOrWhiteSpace(x.name))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var required = new[]
        {
            "externalInvoiceId",
            "upr",
            "mdaCode",
            "serviceCode",
            "revenueCode",
            "taxpayerReference",
            "payerDisplayName",
            "description",
            "amount",
            "currency",
            "expiresAt"
        };
        var missing = required.Where(x => !headerMap.ContainsKey(x)).ToArray();
        if (missing.Length > 0)
        {
            return new BillCsvParseResult([], [$"CSV is missing required columns: {string.Join(", ", missing)}"]);
        }

        var bills = new List<CreateBillRequest>();
        var errors = new List<string>();
        var rowNumber = 1;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var values = ParseCsvLine(line);
                bills.Add(ParseBillCsvRow(headerMap, values, rowNumber));
            }
            catch (FormatException ex)
            {
                errors.Add($"row {rowNumber}: {ex.Message}");
            }
        }

        if (bills.Count == 0 && errors.Count == 0)
        {
            errors.Add("CSV must contain at least one bill row");
        }

        return new BillCsvParseResult(bills, errors);
    }

    private static CreateBillRequest ParseBillCsvRow(IReadOnlyDictionary<string, int> headerMap, IReadOnlyList<string> values, int rowNumber)
    {
        var amountText = GetCsvValue(headerMap, values, "amount");
        if (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            throw new FormatException("amount must be a valid decimal number");
        }

        var dueDateText = GetCsvValue(headerMap, values, "dueDate", required: false);
        DateOnly? dueDate = null;
        if (!string.IsNullOrWhiteSpace(dueDateText))
        {
            if (!DateOnly.TryParse(dueDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDueDate))
            {
                throw new FormatException("dueDate must be an ISO date such as 2026-07-31");
            }

            dueDate = parsedDueDate;
        }

        var expiresAtText = GetCsvValue(headerMap, values, "expiresAt");
        if (!DateTimeOffset.TryParse(expiresAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
        {
            throw new FormatException("expiresAt must be an ISO timestamp");
        }

        Guid? revenueAccountId = null;
        var revenueAccountIdText = GetCsvValue(headerMap, values, "revenueAccountId", required: false);
        if (!string.IsNullOrWhiteSpace(revenueAccountIdText))
        {
            if (!Guid.TryParse(revenueAccountIdText, out var parsedRevenueAccountId))
            {
                throw new FormatException("revenueAccountId must be a valid GUID");
            }

            revenueAccountId = parsedRevenueAccountId;
        }

        return new CreateBillRequest(
            GetCsvValue(headerMap, values, "externalInvoiceId"),
            GetCsvValue(headerMap, values, "upr"),
            GetCsvValue(headerMap, values, "mdaCode"),
            GetCsvValue(headerMap, values, "serviceCode"),
            GetCsvValue(headerMap, values, "revenueCode"),
            GetCsvValue(headerMap, values, "taxpayerReference"),
            GetCsvValue(headerMap, values, "payerDisplayName"),
            GetCsvValue(headerMap, values, "description"),
            amount,
            GetCsvValue(headerMap, values, "currency"),
            dueDate,
            expiresAt,
            revenueAccountId,
            null);
    }

    private static string GetCsvValue(IReadOnlyDictionary<string, int> headerMap, IReadOnlyList<string> values, string column, bool required = true)
    {
        if (!headerMap.TryGetValue(column, out var index) || index >= values.Count)
        {
            if (required)
            {
                throw new FormatException($"{column} is required");
            }

            return "";
        }

        var value = values[index].Trim();
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"{column} is required");
        }

        return value;
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(ch);
            }
        }

        if (inQuotes)
        {
            throw new FormatException("CSV row has an unterminated quoted field");
        }

        values.Add(value.ToString());
        return values.ToArray();
    }

    private sealed record BillCsvParseResult(IReadOnlyList<CreateBillRequest> Bills, IReadOnlyList<string> Errors);
}
