using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.RateLimiting;
using Sps.P2G.Gateway.Infrastructure.Security;
using Sps.P2G.Gateway.Modules.Shared;

namespace Sps.P2G.Gateway.Modules.Reconciliation;

public static class ReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/authorities/{authorityId:guid}")
            .WithTags("Reconciliation")
            .RequireRateLimiting(RateLimitPolicies.Authority);
        var authOptions = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var keycloakOptions = app.ServiceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        if (keycloakOptions.Enabled && !authOptions.AllowAuthorityApiKeyFallback)
        {
            group.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        group.MapGet("/payments", async (
            Guid authorityId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? upr,
            string? externalInvoiceId,
            string? txId,
            string? status,
            string? mdaCode,
            string? serviceCode,
            Guid? revenueAccountId,
            string? accountNumber,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var normalizedPage = NormalizePage(page);
            var normalizedPageSize = NormalizePageSize(pageSize);
            var query = FilterPayments(db, authority.Id, from, to, upr, externalInvoiceId, txId, status, mdaCode, serviceCode, revenueAccountId, accountNumber);
            var total = await query.CountAsync(cancellationToken);
            var pageQuery = query
                .OrderByDescending(x => x.PaidAt)
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize);
            var items = await ProjectPayments(pageQuery, db)
                .ToListAsync(cancellationToken);

            return Results.Ok(new { page = normalizedPage, pageSize = normalizedPageSize, total, items });
        });

        group.MapGet("/payments/export.csv", async (
            Guid authorityId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? upr,
            string? externalInvoiceId,
            string? txId,
            string? status,
            string? mdaCode,
            string? serviceCode,
            Guid? revenueAccountId,
            string? accountNumber,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var rows = await ProjectPayments(
                    FilterPayments(db, authority.Id, from, to, upr, externalInvoiceId, txId, status, mdaCode, serviceCode, revenueAccountId, accountNumber)
                        .OrderByDescending(x => x.PaidAt)
                        .Take(10_000),
                    db)
                .ToListAsync(cancellationToken);

            return Results.Text(ToPaymentCsv(rows), "text/csv; charset=utf-8");
        });

        group.MapGet("/reversals", async (
            Guid authorityId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? upr,
            string? externalInvoiceId,
            string? txId,
            string? mdaCode,
            string? serviceCode,
            Guid? revenueAccountId,
            string? accountNumber,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var normalizedPage = NormalizePage(page);
            var normalizedPageSize = NormalizePageSize(pageSize);
            var query = FilterReversals(db, authority.Id, from, to, upr, externalInvoiceId, txId, mdaCode, serviceCode, revenueAccountId, accountNumber);
            var total = await query.CountAsync(cancellationToken);
            var pageQuery = query
                .OrderByDescending(x => x.ReversedAt)
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize);
            var items = await ProjectReversals(pageQuery, db)
                .ToListAsync(cancellationToken);

            return Results.Ok(new { page = normalizedPage, pageSize = normalizedPageSize, total, items });
        });

        group.MapGet("/reversals/export.csv", async (
            Guid authorityId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? upr,
            string? externalInvoiceId,
            string? txId,
            string? mdaCode,
            string? serviceCode,
            Guid? revenueAccountId,
            string? accountNumber,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var rows = await ProjectReversals(
                    FilterReversals(db, authority.Id, from, to, upr, externalInvoiceId, txId, mdaCode, serviceCode, revenueAccountId, accountNumber)
                        .OrderByDescending(x => x.ReversedAt)
                        .Take(10_000),
                    db)
                .ToListAsync(cancellationToken);

            return Results.Text(ToReversalCsv(rows), "text/csv; charset=utf-8");
        });

        return app;
    }

    private static IQueryable<Domain.Payments.BillPayment> FilterPayments(
        GatewayDbContext db,
        Guid authorityId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? upr,
        string? externalInvoiceId,
        string? txId,
        string? status,
        string? mdaCode,
        string? serviceCode,
        Guid? revenueAccountId,
        string? accountNumber)
    {
        var query = db.BillPayments.Where(x => x.AuthorityId == authorityId);
        if (from is not null) query = query.Where(x => x.PaidAt >= from);
        if (to is not null) query = query.Where(x => x.PaidAt <= to);
        if (!string.IsNullOrWhiteSpace(txId)) query = query.Where(x => x.TxId == txId || x.SipsTransactionId == txId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var billIds = FilterBillIds(db, authorityId, upr, externalInvoiceId, mdaCode, serviceCode, revenueAccountId, accountNumber);
        if (billIds is not null)
        {
            query = query.Where(x => billIds.Contains(x.BillId));
        }

        return query;
    }

    private static IQueryable<Domain.Payments.BillReversal> FilterReversals(
        GatewayDbContext db,
        Guid authorityId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? upr,
        string? externalInvoiceId,
        string? txId,
        string? mdaCode,
        string? serviceCode,
        Guid? revenueAccountId,
        string? accountNumber)
    {
        var query = db.BillReversals.Where(x => x.AuthorityId == authorityId);
        if (from is not null) query = query.Where(x => x.ReversedAt >= from);
        if (to is not null) query = query.Where(x => x.ReversedAt <= to);
        if (!string.IsNullOrWhiteSpace(txId)) query = query.Where(x => x.OriginalSipsTransactionId == txId || x.ReversalTransactionId == txId);
        var billIds = FilterBillIds(db, authorityId, upr, externalInvoiceId, mdaCode, serviceCode, revenueAccountId, accountNumber);
        if (billIds is not null)
        {
            query = query.Where(x => billIds.Contains(x.BillId));
        }

        return query;
    }

    private static IQueryable<Guid>? FilterBillIds(
        GatewayDbContext db,
        Guid authorityId,
        string? upr,
        string? externalInvoiceId,
        string? mdaCode,
        string? serviceCode,
        Guid? revenueAccountId,
        string? accountNumber)
    {
        if (string.IsNullOrWhiteSpace(upr) &&
            string.IsNullOrWhiteSpace(externalInvoiceId) &&
            string.IsNullOrWhiteSpace(mdaCode) &&
            string.IsNullOrWhiteSpace(serviceCode) &&
            revenueAccountId is null &&
            string.IsNullOrWhiteSpace(accountNumber))
        {
            return null;
        }

        var query = db.Bills.Where(x => x.AuthorityId == authorityId);
        if (!string.IsNullOrWhiteSpace(upr)) query = query.Where(x => x.UPR == upr);
        if (!string.IsNullOrWhiteSpace(externalInvoiceId)) query = query.Where(x => x.ExternalInvoiceId == externalInvoiceId);
        if (!string.IsNullOrWhiteSpace(mdaCode)) query = query.Where(x => x.MdaCode == mdaCode.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(serviceCode)) query = query.Where(x => x.ServiceCode == serviceCode.Trim().ToUpperInvariant());
        if (revenueAccountId is not null) query = query.Where(x => x.RevenueAccountId == revenueAccountId.Value);
        if (!string.IsNullOrWhiteSpace(accountNumber))
        {
            var accountIds = db.RevenueAccounts
                .Where(x => x.AuthorityId == authorityId && x.AccountNumber == accountNumber.Trim())
                .Select(x => x.Id);
            query = query.Where(x => accountIds.Contains(x.RevenueAccountId));
        }

        return query.Select(x => x.Id);
    }

    private static IQueryable<PaymentProjection> ProjectPayments(IQueryable<Domain.Payments.BillPayment> query, GatewayDbContext db) =>
        from payment in query
        join bill in db.Bills on payment.BillId equals bill.Id
        join account in db.RevenueAccounts on bill.RevenueAccountId equals account.Id
        select new PaymentProjection(
            payment.Id,
            payment.BillId,
            bill.UPR,
            bill.ExternalInvoiceId,
            bill.MdaCode,
            account.MdaName,
            bill.ServiceCode,
            account.ServiceName,
            bill.RevenueCode,
            bill.RevenueAccountId,
            account.AccountNumber,
            account.AccountName,
            payment.TxId,
            payment.SipsTransactionId,
            payment.EndToEndId,
            payment.OriginalMessageId,
            payment.Amount,
            payment.Currency,
            payment.Status,
            payment.PaidAt,
            payment.PayerName,
            payment.PayerAccount,
            payment.PayerBankCode,
            payment.PayeeName,
            payment.PayeeAccount,
            payment.PayeeBankCode);

    private static IQueryable<ReversalProjection> ProjectReversals(IQueryable<Domain.Payments.BillReversal> query, GatewayDbContext db) =>
        from reversal in query
        join bill in db.Bills on reversal.BillId equals bill.Id
        join account in db.RevenueAccounts on bill.RevenueAccountId equals account.Id
        select new ReversalProjection(
            reversal.Id,
            reversal.BillId,
            bill.UPR,
            bill.ExternalInvoiceId,
            bill.MdaCode,
            account.MdaName,
            bill.ServiceCode,
            account.ServiceName,
            bill.RevenueCode,
            bill.RevenueAccountId,
            account.AccountNumber,
            account.AccountName,
            reversal.OriginalSipsTransactionId,
            reversal.ReversalTransactionId,
            reversal.Amount,
            reversal.Currency,
            reversal.Reason,
            reversal.ReversedAt);

    private static int NormalizePage(int? page) => page is null or <= 0 ? 1 : page.Value;
    private static int NormalizePageSize(int? pageSize) => pageSize is null or <= 0 or > 200 ? 50 : pageSize.Value;

    private static string ToPaymentCsv(IEnumerable<PaymentProjection> rows)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("paymentId,billId,upr,externalInvoiceId,mdaCode,mdaName,serviceCode,serviceName,revenueCode,revenueAccountId,revenueAccountNumber,revenueAccountName,txId,sipsTransactionId,endToEndId,amount,currency,status,paidAt,payerName,payerAccount,payerBankCode,payeeName,payeeAccount,payeeBankCode");
        foreach (var row in rows)
        {
            AppendCsvLine(builder,
                row.PaymentId,
                row.BillId,
                row.UPR,
                row.ExternalInvoiceId,
                row.MdaCode,
                row.MdaName,
                row.ServiceCode,
                row.ServiceName,
                row.RevenueCode,
                row.RevenueAccountId,
                row.RevenueAccountNumber,
                row.RevenueAccountName,
                row.TxId,
                row.SipsTransactionId,
                row.EndToEndId,
                row.Amount,
                row.Currency,
                row.Status,
                row.PaidAt,
                row.PayerName,
                row.PayerAccount,
                row.PayerBankCode,
                row.PayeeName,
                row.PayeeAccount,
                row.PayeeBankCode);
        }

        return builder.ToString();
    }

    private static string ToReversalCsv(IEnumerable<ReversalProjection> rows)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("reversalId,billId,upr,externalInvoiceId,mdaCode,mdaName,serviceCode,serviceName,revenueCode,revenueAccountId,revenueAccountNumber,revenueAccountName,originalSipsTransactionId,reversalTransactionId,amount,currency,reason,reversedAt");
        foreach (var row in rows)
        {
            AppendCsvLine(builder,
                row.ReversalId,
                row.BillId,
                row.UPR,
                row.ExternalInvoiceId,
                row.MdaCode,
                row.MdaName,
                row.ServiceCode,
                row.ServiceName,
                row.RevenueCode,
                row.RevenueAccountId,
                row.RevenueAccountNumber,
                row.RevenueAccountName,
                row.OriginalSipsTransactionId,
                row.ReversalTransactionId,
                row.Amount,
                row.Currency,
                row.Reason,
                row.ReversedAt);
        }

        return builder.ToString();
    }

    private static void AppendCsvLine(System.Text.StringBuilder builder, params object?[] values)
    {
        builder.AppendLine(string.Join(',', values.Select(value => EscapeCsv(value?.ToString() ?? ""))));
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains('"', StringComparison.Ordinal) &&
            !value.Contains(',', StringComparison.Ordinal) &&
            !value.Contains('\n', StringComparison.Ordinal) &&
            !value.Contains('\r', StringComparison.Ordinal))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

internal sealed record PaymentProjection(
    Guid PaymentId,
    Guid BillId,
    string UPR,
    string ExternalInvoiceId,
    string MdaCode,
    string MdaName,
    string ServiceCode,
    string ServiceName,
    string RevenueCode,
    Guid RevenueAccountId,
    string RevenueAccountNumber,
    string RevenueAccountName,
    string TxId,
    string SipsTransactionId,
    string EndToEndId,
    string OriginalMessageId,
    decimal Amount,
    string Currency,
    string Status,
    DateTimeOffset PaidAt,
    string PayerName,
    string PayerAccount,
    string PayerBankCode,
    string PayeeName,
    string PayeeAccount,
    string PayeeBankCode);

internal sealed record ReversalProjection(
    Guid ReversalId,
    Guid BillId,
    string UPR,
    string ExternalInvoiceId,
    string MdaCode,
    string MdaName,
    string ServiceCode,
    string ServiceName,
    string RevenueCode,
    Guid RevenueAccountId,
    string RevenueAccountNumber,
    string RevenueAccountName,
    string OriginalSipsTransactionId,
    string ReversalTransactionId,
    decimal Amount,
    string Currency,
    string Reason,
    DateTimeOffset ReversedAt);
