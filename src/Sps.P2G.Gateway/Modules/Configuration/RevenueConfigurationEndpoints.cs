using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Domain.Authorities;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.RateLimiting;
using Sps.P2G.Gateway.Infrastructure.Security;
using Sps.P2G.Gateway.Infrastructure.Time;
using Sps.P2G.Gateway.Modules.Authorities;
using Sps.P2G.Gateway.Modules.Shared;

namespace Sps.P2G.Gateway.Modules.Configuration;

public static class RevenueConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapRevenueConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var keycloakOptions = app.ServiceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        var authOptions = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var strictAuthorityAuth = keycloakOptions.Enabled && !authOptions.AllowAuthorityApiKeyFallback;

        var dashboardGroup = app.MapGroup("/api/v1/dashboard/authorities/{authorityId:guid}/revenue-accounts")
            .WithTags("Revenue Configuration")
            .RequireRateLimiting(RateLimitPolicies.Dashboard);
        if (keycloakOptions.Enabled)
        {
            dashboardGroup.RequireAuthorization(AuthPolicies.ConfigurationAdmin);
        }

        dashboardGroup.MapGet("", async (
            Guid authorityId,
            GatewayDbContext db,
            string? mdaCode,
            string? serviceCode,
            string? currency,
            string? status,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            if (!await db.RevenueAuthorities.AnyAsync(x => x.Id == authorityId, cancellationToken))
            {
                return Results.NotFound(new { error = "authority was not found" });
            }

            return Results.Ok(await ListRevenueAccountsAsync(db, authorityId, mdaCode, serviceCode, currency, status, page, pageSize, cancellationToken));
        });

        dashboardGroup.MapPost("", async (
            Guid authorityId,
            CreateRevenueAccountRequest request,
            GatewayDbContext db,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await db.RevenueAuthorities.FirstOrDefaultAsync(x => x.Id == authorityId, cancellationToken);
            if (authority is null)
            {
                return Results.NotFound(new { error = "authority was not found" });
            }

            return await CreateRevenueAccountAsync(authority, request, db, clock, cancellationToken);
        });

        dashboardGroup.MapPatch("/{accountId:guid}", async (
            Guid authorityId,
            Guid accountId,
            UpdateRevenueAccountRequest request,
            GatewayDbContext db,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var account = await db.RevenueAccounts.FirstOrDefaultAsync(x => x.Id == accountId && x.AuthorityId == authorityId, cancellationToken);
            if (account is null)
            {
                return Results.NotFound();
            }

            var validationError = ValidateUpdateRevenueAccountRequest(request);
            if (validationError is not null)
            {
                return Results.BadRequest(new { error = validationError });
            }

            ApplyRevenueAccountUpdate(account, request);
            account.UpdatedAt = clock.UtcNow;
            if (account.IsDefault)
            {
                await ClearDefaultRevenueAccountsAsync(db, account.AuthorityId, account.MdaCode, account.ServiceCode, account.Currency, account.Id, clock.UtcNow, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(ProjectRevenueAccount(account));
        });

        var authorityGroup = app.MapGroup("/api/v1/authorities/{authorityId:guid}/accounts")
            .WithTags("Revenue Configuration")
            .RequireRateLimiting(RateLimitPolicies.Authority);
        if (strictAuthorityAuth)
        {
            authorityGroup.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        authorityGroup.MapGet("", async (
            Guid authorityId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            string? mdaCode,
            string? serviceCode,
            string? currency,
            string? status,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await ListRevenueAccountsAsync(db, authority.Id, mdaCode, serviceCode, currency, status ?? "Active", page, pageSize, cancellationToken));
        });

        authorityGroup.MapGet("/{accountId:guid}", async (
            Guid authorityId,
            Guid accountId,
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

            var account = await db.RevenueAccounts.FirstOrDefaultAsync(x => x.Id == accountId && x.AuthorityId == authority.Id, cancellationToken);
            return account is null ? Results.NotFound() : Results.Ok(ProjectRevenueAccount(account));
        });

        return app;
    }

    public static async Task<IResult> CreateRevenueAccountAsync(
        RevenueAuthority authority,
        CreateRevenueAccountRequest request,
        GatewayDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateCreateRevenueAccountRequest(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        var now = clock.UtcNow;
        var account = new RevenueAccount
        {
            Id = Guid.NewGuid(),
            AuthorityId = authority.Id,
            MdaCode = request.MdaCode.Trim().ToUpperInvariant(),
            MdaName = request.MdaName?.Trim() ?? "",
            ServiceCode = request.ServiceCode.Trim().ToUpperInvariant(),
            ServiceName = request.ServiceName?.Trim() ?? "",
            RevenueCode = request.RevenueCode?.Trim().ToUpperInvariant() ?? "",
            BankCode = request.BankCode?.Trim() ?? "",
            BankName = request.BankName?.Trim() ?? "",
            AccountNumber = request.AccountNumber.Trim(),
            AccountName = request.AccountName.Trim(),
            ClearingType = string.IsNullOrWhiteSpace(request.ClearingType) ? "ACCT" : request.ClearingType.Trim().ToUpperInvariant(),
            BillerBic = request.BillerBic?.Trim() ?? "",
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim().ToUpperInvariant(),
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now
        };

        var hasDefaultForCombination = await db.RevenueAccounts.AnyAsync(
            x => x.AuthorityId == authority.Id &&
                 x.MdaCode == account.MdaCode &&
                 x.ServiceCode == account.ServiceCode &&
                 x.Currency == account.Currency &&
                 x.IsDefault,
            cancellationToken);
        account.IsDefault = request.IsDefault || !hasDefaultForCombination;

        if (account.IsDefault)
        {
            await ClearDefaultRevenueAccountsAsync(db, authority.Id, account.MdaCode, account.ServiceCode, account.Currency, null, now, cancellationToken);
        }

        db.RevenueAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/dashboard/authorities/{authority.Id}/revenue-accounts/{account.Id}", ProjectRevenueAccount(account));
    }

    public static object ProjectRevenueAccount(RevenueAccount account) => new
    {
        account.Id,
        account.AuthorityId,
        account.MdaCode,
        account.MdaName,
        account.ServiceCode,
        account.ServiceName,
        account.RevenueCode,
        account.BankCode,
        account.BankName,
        account.AccountNumber,
        account.AccountName,
        account.ClearingType,
        account.BillerBic,
        account.Currency,
        account.IsDefault,
        account.Status,
        account.CreatedAt,
        account.UpdatedAt
    };

    private static async Task<object> ListRevenueAccountsAsync(
        GatewayDbContext db,
        Guid authorityId,
        string? mdaCode,
        string? serviceCode,
        string? currency,
        string? status,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var normalizedPage = page is null or <= 0 ? 1 : page.Value;
        var normalizedPageSize = pageSize is null or <= 0 or > 200 ? 50 : pageSize.Value;
        var query = db.RevenueAccounts.Where(x => x.AuthorityId == authorityId);
        if (!string.IsNullOrWhiteSpace(mdaCode)) query = query.Where(x => x.MdaCode == mdaCode.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(serviceCode)) query = query.Where(x => x.ServiceCode == serviceCode.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(currency)) query = query.Where(x => x.Currency == currency.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim());

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.MdaCode)
            .ThenBy(x => x.ServiceCode)
            .ThenByDescending(x => x.IsDefault)
            .ThenBy(x => x.Currency)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken);

        return new
        {
            page = normalizedPage,
            pageSize = normalizedPageSize,
            total,
            items = items.Select(ProjectRevenueAccount)
        };
    }

    private static string? ValidateCreateRevenueAccountRequest(CreateRevenueAccountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MdaCode)) return "mdaCode is required";
        if (string.IsNullOrWhiteSpace(request.ServiceCode)) return "serviceCode is required";
        if (string.IsNullOrWhiteSpace(request.AccountNumber)) return "accountNumber is required";
        if (string.IsNullOrWhiteSpace(request.AccountName)) return "accountName is required";
        return ValidateCurrency(request.Currency);
    }

    private static string? ValidateUpdateRevenueAccountRequest(UpdateRevenueAccountRequest request)
    {
        if (request.AccountNumber is not null && string.IsNullOrWhiteSpace(request.AccountNumber)) return "accountNumber cannot be empty";
        if (request.AccountName is not null && string.IsNullOrWhiteSpace(request.AccountName)) return "accountName cannot be empty";
        if (request.Status is not null && request.Status.Trim() is not ("Active" or "Inactive")) return "status must be Active or Inactive";
        return ValidateCurrency(request.Currency);
    }

    private static string? ValidateCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        return currency.Trim().ToUpperInvariant() is "USD" or "SOS" ? null : "currency must be USD or SOS";
    }

    private static void ApplyRevenueAccountUpdate(RevenueAccount account, UpdateRevenueAccountRequest request)
    {
        if (request.MdaName is not null) account.MdaName = request.MdaName.Trim();
        if (request.ServiceName is not null) account.ServiceName = request.ServiceName.Trim();
        if (request.RevenueCode is not null) account.RevenueCode = request.RevenueCode.Trim().ToUpperInvariant();
        if (request.BankCode is not null) account.BankCode = request.BankCode.Trim();
        if (request.BankName is not null) account.BankName = request.BankName.Trim();
        if (request.AccountNumber is not null) account.AccountNumber = request.AccountNumber.Trim();
        if (request.AccountName is not null) account.AccountName = request.AccountName.Trim();
        if (request.ClearingType is not null) account.ClearingType = string.IsNullOrWhiteSpace(request.ClearingType) ? "ACCT" : request.ClearingType.Trim().ToUpperInvariant();
        if (request.BillerBic is not null) account.BillerBic = request.BillerBic.Trim();
        if (request.Currency is not null) account.Currency = request.Currency.Trim().ToUpperInvariant();
        if (request.IsDefault is not null) account.IsDefault = request.IsDefault.Value;
        if (request.Status is not null) account.Status = request.Status.Trim();
    }

    private static async Task ClearDefaultRevenueAccountsAsync(
        GatewayDbContext db,
        Guid authorityId,
        string mdaCode,
        string serviceCode,
        string currency,
        Guid? exceptAccountId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existingDefaults = await db.RevenueAccounts
            .Where(x => x.AuthorityId == authorityId &&
                        x.MdaCode == mdaCode &&
                        x.ServiceCode == serviceCode &&
                        x.Currency == currency &&
                        x.IsDefault &&
                        (exceptAccountId == null || x.Id != exceptAccountId))
            .ToListAsync(cancellationToken);

        foreach (var existingDefault in existingDefaults)
        {
            existingDefault.IsDefault = false;
            existingDefault.UpdatedAt = now;
        }
    }
}

public sealed record UpdateRevenueAccountRequest(
    string? MdaName,
    string? ServiceName,
    string? RevenueCode,
    string? BankCode,
    string? BankName,
    string? AccountNumber,
    string? AccountName,
    string? ClearingType,
    string? BillerBic,
    string? Currency,
    bool? IsDefault,
    string? Status);
