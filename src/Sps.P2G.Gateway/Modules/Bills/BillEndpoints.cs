using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Domain.Authorities;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Domain.Security;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.RateLimiting;
using Sps.P2G.Gateway.Infrastructure.Security;
using Sps.P2G.Gateway.Infrastructure.Time;
using Sps.P2G.Gateway.Modules.Shared;

namespace Sps.P2G.Gateway.Modules.Bills;

public static class BillEndpoints
{
    public static IEndpointRouteBuilder MapBillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/authorities/{authorityId:guid}")
            .WithTags("Bills")
            .RequireRateLimiting(RateLimitPolicies.Authority);
        var authOptions = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var keycloakOptions = app.ServiceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        if (keycloakOptions.Enabled && !authOptions.AllowAuthorityApiKeyFallback)
        {
            group.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        group.MapPost("/bills", async (
            Guid authorityId,
            CreateBillRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IHashChainService hashChain,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var result = await CreateBillAsync(authority, request, db, hashChain, clock, httpContext.TraceIdentifier, cancellationToken);
            return result.ToHttpResult();
        });

        group.MapPost("/bills/bulk", async (
            Guid authorityId,
            CreateBillRequest[] requests,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IHashChainService hashChain,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var results = new List<object>();
            foreach (var request in requests)
            {
                var result = await CreateBillAsync(authority, request, db, hashChain, clock, httpContext.TraceIdentifier, cancellationToken);
                results.Add(new
                {
                    request.ExternalInvoiceId,
                    request.Upr,
                    status = result.Status,
                    result.BillId,
                    result.Error
                });
            }

            return Results.Ok(new { results });
        });

        group.MapPost("/bills/{billId:guid}/cancel", async (
            Guid authorityId,
            Guid billId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IHashChainService hashChain,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var bill = await db.Bills.FirstOrDefaultAsync(x => x.AuthorityId == authority.Id && x.Id == billId, cancellationToken);
            if (bill is null)
            {
                return Results.NotFound();
            }

            if (bill.StatusProjection is not (BillStatuses.Available or BillStatuses.Fetched or BillStatuses.PaymentInitiated))
            {
                return Results.Conflict(new { error = $"bill cannot be cancelled from {bill.StatusProjection}" });
            }

            var hasSuccessfulPayment = await db.BillPayments.AnyAsync(x => x.BillId == bill.Id && x.Status == "ACSC", cancellationToken);
            if (hasSuccessfulPayment)
            {
                return Results.Conflict(new { error = "paid bill cannot be cancelled" });
            }

            bill.StatusProjection = BillStatuses.Cancelled;
            bill.UpdatedAt = clock.UtcNow;
            await hashChain.AppendAsync(db, bill, BillEventTypes.BillCancelled, new { bill.Id, bill.UPR }, "Authority", authority.Code, "RevenueAuthorityApi", httpContext.TraceIdentifier, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(ProjectBill(bill));
        });

        group.MapGet("/bills", async (
            Guid authorityId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
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
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var query = FilterBills(db.Bills.Where(x => x.AuthorityId == authority.Id), upr, externalInvoiceId, taxpayerReference, mdaCode, serviceCode, status);
            page = page <= 0 ? 1 : page;
            pageSize = pageSize is <= 0 or > 200 ? 50 : pageSize;
            var total = await query.CountAsync(cancellationToken);
            var items = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).Select(x => ProjectBill(x)).ToListAsync(cancellationToken);
            return Results.Ok(new { page, pageSize, total, items });
        });

        group.MapGet("/bills/{billId:guid}", async (
            Guid authorityId,
            Guid billId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var bill = await db.Bills.FirstOrDefaultAsync(x => x.AuthorityId == authority.Id && x.Id == billId, cancellationToken);
            return bill is null ? Results.NotFound() : Results.Ok(ProjectBill(bill));
        });

        group.MapGet("/bill-events", async (
            Guid authorityId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            long cursor,
            string? types,
            Guid? billId,
            bool? includePayload,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var requestedTypes = string.IsNullOrWhiteSpace(types)
                ? []
                : types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var query = db.BillEvents.Where(x => x.AuthorityId == authority.Id && x.Sequence > cursor);
            if (billId is not null)
            {
                query = query.Where(x => x.BillId == billId);
            }

            if (requestedTypes.Length > 0)
            {
                query = query.Where(x => requestedTypes.Contains(x.EventType));
            }

            var events = await query.OrderBy(x => x.Sequence).Take(200).ToListAsync(cancellationToken);
            var nextCursor = events.Count == 0 ? cursor : events.Max(x => x.Sequence);
            return Results.Ok(new
            {
                hasNewEvents = events.Count > 0,
                nextCursor = nextCursor.ToString(),
                events = events.Select(x => new
                {
                    sequence = x.Sequence,
                    eventId = x.Id,
                    eventType = x.EventType,
                    billId = x.BillId,
                    eventTime = x.EventTime,
                    actorType = x.ActorType,
                    actorId = x.ActorId,
                    sourceSystem = x.SourceSystem,
                    correlationId = x.CorrelationId,
                    payloadHash = x.PayloadHash,
                    previousEventHash = x.PreviousEventHash,
                    eventHash = x.EventHash,
                    payloadJson = includePayload == true ? x.PayloadJson : null
                })
            });
        });

        return app;
    }

    internal static async Task<CreateBillResult> CreateBillAsync(
        RevenueAuthority authority,
        CreateBillRequest request,
        GatewayDbContext db,
        IHashChainService hashChain,
        IClock clock,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateBillRequest(request, clock.UtcNow);
        if (validationError is not null)
        {
            return CreateBillResult.BadRequest(validationError);
        }

        var upr = request.Upr.Trim();
        var externalInvoiceId = request.ExternalInvoiceId.Trim();
        var payloadJson = JsonDefaults.Serialize(request);
        var payloadHash = JsonDefaults.Sha256(payloadJson);
        var idempotencyKey = $"bill:create:{authority.Id}:{externalInvoiceId}";
        var idempotencyRecord = await db.IdempotencyRecords.FirstOrDefaultAsync(x => x.Key == idempotencyKey, cancellationToken);
        if (idempotencyRecord is not null)
        {
            if (idempotencyRecord.PayloadHash != payloadHash)
            {
                return CreateBillResult.Conflict("externalInvoiceId already exists with different payload");
            }

            var idempotentBill = idempotencyRecord.ResourceId is { } resourceId
                ? await db.Bills.FirstOrDefaultAsync(x => x.Id == resourceId && x.AuthorityId == authority.Id, cancellationToken)
                : null;
            if (idempotentBill is not null)
            {
                return CreateBillResult.Ok(idempotentBill, "idempotent");
            }
        }

        var existing = await db.Bills.FirstOrDefaultAsync(
            x => x.AuthorityId == authority.Id && x.ExternalInvoiceId == externalInvoiceId,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.PayloadHash == payloadHash && idempotencyRecord is null)
            {
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    Key = idempotencyKey,
                    PayloadHash = payloadHash,
                    ResourceId = existing.Id,
                    CreatedAt = clock.UtcNow
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            return existing.PayloadHash == payloadHash
                ? CreateBillResult.Ok(existing, "idempotent")
                : CreateBillResult.Conflict("externalInvoiceId already exists with different payload");
        }

        if (await db.Bills.AnyAsync(x => x.AuthorityId == authority.Id && x.UPR == upr, cancellationToken))
        {
            return CreateBillResult.Conflict("UPR already exists");
        }

        var account = await ResolveRevenueAccountAsync(authority.Id, request, db, cancellationToken);
        if (account is null)
        {
            return CreateBillResult.BadRequest("matching revenue account was not found");
        }

        var now = clock.UtcNow;
        var bill = new Bill
        {
            Id = Guid.NewGuid(),
            AuthorityId = authority.Id,
            ExternalInvoiceId = externalInvoiceId,
            UPR = upr,
            MdaCode = request.MdaCode.Trim().ToUpperInvariant(),
            ServiceCode = request.ServiceCode.Trim().ToUpperInvariant(),
            RevenueCode = request.RevenueCode.Trim().ToUpperInvariant(),
            TaxpayerReference = request.TaxpayerReference.Trim(),
            PayerDisplayName = request.PayerDisplayName.Trim(),
            Description = request.Description.Trim(),
            Amount = request.Amount,
            Currency = request.Currency.Trim().ToUpperInvariant(),
            DueDate = request.DueDate,
            ExpiresAt = request.ExpiresAt,
            RevenueAccountId = account.Id,
            StatusProjection = BillStatuses.Available,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedPayloadJson = payloadJson,
            PayloadHash = payloadHash
        };
        bill.RecordHash = JsonDefaults.Sha256($"{bill.Id}|{bill.AuthorityId}|{bill.UPR}|{bill.ExternalInvoiceId}|{bill.Amount:0.00}|{bill.Currency}|{payloadHash}");

        db.Bills.Add(bill);
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            Key = idempotencyKey,
            PayloadHash = payloadHash,
            ResourceId = bill.Id,
            CreatedAt = now
        });
        await hashChain.AppendAsync(db, bill, BillEventTypes.BillCreated, new
        {
            bill.Id,
            bill.UPR,
            bill.ExternalInvoiceId,
            bill.Amount,
            bill.Currency
        }, "Authority", authority.Code, "RevenueAuthorityApi", correlationId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return CreateBillResult.Ok(bill, "created");
    }

    internal static IQueryable<Bill> FilterBills(
        IQueryable<Bill> query,
        string? upr,
        string? externalInvoiceId,
        string? taxpayerReference,
        string? mdaCode,
        string? serviceCode,
        string? status)
    {
        if (!string.IsNullOrWhiteSpace(upr)) query = query.Where(x => x.UPR == upr);
        if (!string.IsNullOrWhiteSpace(externalInvoiceId)) query = query.Where(x => x.ExternalInvoiceId == externalInvoiceId);
        if (!string.IsNullOrWhiteSpace(taxpayerReference)) query = query.Where(x => x.TaxpayerReference == taxpayerReference);
        if (!string.IsNullOrWhiteSpace(mdaCode)) query = query.Where(x => x.MdaCode == mdaCode.ToUpper());
        if (!string.IsNullOrWhiteSpace(serviceCode)) query = query.Where(x => x.ServiceCode == serviceCode.ToUpper());
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.StatusProjection == status);
        return query;
    }

    internal static object ProjectBill(Bill bill) => new
    {
        billId = bill.Id,
        bill.UPR,
        bill.ExternalInvoiceId,
        status = bill.StatusProjection,
        bill.Amount,
        bill.Currency,
        bill.MdaCode,
        bill.ServiceCode,
        bill.RevenueCode,
        bill.TaxpayerReference,
        bill.ExpiresAt,
        bill.RecordHash
    };

    private static string? ValidateBillRequest(CreateBillRequest request, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalInvoiceId)) return "externalInvoiceId is required";
        if (string.IsNullOrWhiteSpace(request.Upr)) return "upr is required";
        if (string.IsNullOrWhiteSpace(request.MdaCode)) return "mdaCode is required";
        if (string.IsNullOrWhiteSpace(request.ServiceCode)) return "serviceCode is required";
        if (string.IsNullOrWhiteSpace(request.RevenueCode)) return "revenueCode is required";
        if (request.Amount <= 0) return "amount must be greater than zero";
        if (string.IsNullOrWhiteSpace(request.Currency)) return "currency is required";
        if (request.Currency.Trim().ToUpperInvariant() is not ("USD" or "SOS")) return "MVP supports USD and SOS only";
        if (request.ExpiresAt <= now) return "expiresAt must be in the future";
        return null;
    }

    private static async Task<RevenueAccount?> ResolveRevenueAccountAsync(
        Guid authorityId,
        CreateBillRequest request,
        GatewayDbContext db,
        CancellationToken cancellationToken)
    {
        if (request.RevenueAccountId is { } revenueAccountId)
        {
            return await db.RevenueAccounts.FirstOrDefaultAsync(x => x.AuthorityId == authorityId && x.Id == revenueAccountId && x.Status == "Active", cancellationToken);
        }

        var mdaCode = request.MdaCode.Trim().ToUpperInvariant();
        var serviceCode = request.ServiceCode.Trim().ToUpperInvariant();
        return await db.RevenueAccounts.FirstOrDefaultAsync(
            x => x.AuthorityId == authorityId &&
                 x.MdaCode == mdaCode &&
                 x.ServiceCode == serviceCode &&
                 x.IsDefault &&
                 x.Status == "Active",
            cancellationToken);
    }
}

public sealed record CreateBillRequest(
    string ExternalInvoiceId,
    string Upr,
    string MdaCode,
    string ServiceCode,
    string RevenueCode,
    string TaxpayerReference,
    string PayerDisplayName,
    string Description,
    decimal Amount,
    string Currency,
    DateOnly? DueDate,
    DateTimeOffset ExpiresAt,
    Guid? RevenueAccountId,
    Dictionary<string, object>? Metadata);

internal sealed record CreateBillResult(string Status, Guid? BillId, string? Error, Bill? Bill)
{
    public static CreateBillResult Ok(Bill bill, string status) => new(status, bill.Id, null, bill);
    public static CreateBillResult BadRequest(string error) => new("invalid", null, error, null);
    public static CreateBillResult Conflict(string error) => new("conflict", null, error, null);

    public IResult ToHttpResult() => Status switch
    {
        "created" => Results.Created($"/api/v1/authorities/{Bill!.AuthorityId}/bills/{Bill.Id}", BillEndpoints.ProjectBill(Bill)),
        "idempotent" => Results.Ok(BillEndpoints.ProjectBill(Bill!)),
        "conflict" => Results.Conflict(new { error = Error }),
        _ => Results.BadRequest(new { error = Error })
    };
}
