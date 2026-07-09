using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Domain.Authorities;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.RateLimiting;
using Sps.P2G.Gateway.Infrastructure.Security;
using Sps.P2G.Gateway.Infrastructure.Time;
using Sps.P2G.Gateway.Modules.Shared;

namespace Sps.P2G.Gateway.Modules.Authorities;

public static class AuthorityEndpoints
{
    public static IEndpointRouteBuilder MapAuthorityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/authorities")
            .WithTags("Authorities")
            .RequireRateLimiting(RateLimitPolicies.Authority);
        var authOptions = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var keycloakOptions = app.ServiceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        var strictAuthorityAuth = keycloakOptions.Enabled && !authOptions.AllowAuthorityApiKeyFallback;

        var createAuthority = group.MapPost("", async (
            CreateAuthorityRequest request,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "code and name are required" });
            }

            var code = request.Code.Trim().ToUpperInvariant();
            if (await db.RevenueAuthorities.AnyAsync(x => x.Code == code, cancellationToken))
            {
                return Results.Conflict(new { error = "authority code already exists" });
            }

            var now = clock.UtcNow;
            var apiKey = hasher.GenerateApiKey();
            var authority = new RevenueAuthority
            {
                Id = Guid.NewGuid(),
                Code = code,
                Name = request.Name.Trim(),
                Status = "Active",
                ApiKeyHash = hasher.Hash(apiKey),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.RevenueAuthorities.Add(authority);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/v1/authorities/{authority.Id}", new
            {
                authority.Id,
                authority.Code,
                authority.Name,
                apiKey
            });
        });
        if (keycloakOptions.Enabled)
        {
            createAuthority.RequireAuthorization(AuthPolicies.DashboardUser);
        }

        var createAccount = group.MapPost("/{authorityId:guid}/accounts", async (
            Guid authorityId,
            CreateRevenueAccountRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.MdaCode) ||
                string.IsNullOrWhiteSpace(request.ServiceCode) ||
                string.IsNullOrWhiteSpace(request.AccountNumber) ||
                string.IsNullOrWhiteSpace(request.AccountName))
            {
                return Results.BadRequest(new { error = "mdaCode, serviceCode, accountNumber, and accountName are required" });
            }

            var now = clock.UtcNow;
            var hasAnyAccount = await db.RevenueAccounts.AnyAsync(x => x.AuthorityId == authority.Id, cancellationToken);
            var account = new RevenueAccount
            {
                Id = Guid.NewGuid(),
                AuthorityId = authority.Id,
                MdaCode = request.MdaCode.Trim().ToUpperInvariant(),
                ServiceCode = request.ServiceCode.Trim().ToUpperInvariant(),
                RevenueCode = request.RevenueCode?.Trim().ToUpperInvariant() ?? "",
                BankCode = request.BankCode?.Trim() ?? "",
                BankName = request.BankName?.Trim() ?? "",
                AccountNumber = request.AccountNumber.Trim(),
                AccountName = request.AccountName.Trim(),
                ClearingType = string.IsNullOrWhiteSpace(request.ClearingType) ? "ACCT" : request.ClearingType.Trim().ToUpperInvariant(),
                BillerBic = request.BillerBic?.Trim() ?? "",
                Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim().ToUpperInvariant(),
                IsDefault = request.IsDefault || !hasAnyAccount,
                Status = "Active",
                CreatedAt = now,
                UpdatedAt = now
            };

            if (account.IsDefault)
            {
                var existingDefaults = await db.RevenueAccounts
                    .Where(x => x.AuthorityId == authority.Id && x.MdaCode == account.MdaCode && x.ServiceCode == account.ServiceCode)
                    .ToListAsync(cancellationToken);
                foreach (var existingDefault in existingDefaults)
                {
                    existingDefault.IsDefault = false;
                    existingDefault.UpdatedAt = now;
                }
            }

            db.RevenueAccounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/v1/authorities/{authority.Id}/accounts/{account.Id}", account);
        });
        if (strictAuthorityAuth)
        {
            createAccount.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        var createCallbackSubscription = group.MapPost("/{authorityId:guid}/callback-subscriptions", async (
            Guid authorityId,
            CreateCallbackSubscriptionRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IApiKeyHasher secretGenerator,
            IDataProtectionProvider dataProtectionProvider,
            IOptions<CallbackSecurityOptions> callbackSecurityOptions,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var callbackUrlValidation = CallbackUrlValidator.Validate(request.CallbackUrl, callbackSecurityOptions.Value);
            if (!callbackUrlValidation.IsValid)
            {
                return Results.BadRequest(new { error = callbackUrlValidation.Error });
            }

            var now = clock.UtcNow;
            var secret = secretGenerator.GenerateApiKey();
            var protectedSecret = dataProtectionProvider
                .CreateProtector("Sps.P2G.CallbackSecret.v1")
                .Protect(secret);
            var subscription = new CallbackSubscription
            {
                Id = Guid.NewGuid(),
                AuthorityId = authority.Id,
                CallbackUrl = callbackUrlValidation.Uri!.ToString(),
                Secret = protectedSecret,
                SubscribedEventsJson = JsonSerializer.Serialize(request.SubscribedEvents ?? [], JsonDefaults.SerializerOptions),
                Enabled = request.Enabled,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.CallbackSubscriptions.Add(subscription);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/v1/authorities/{authority.Id}/callback-subscriptions/{subscription.Id}", new
            {
                subscription.Id,
                subscription.CallbackUrl,
                subscription.Enabled,
                secret
            });
        });
        if (strictAuthorityAuth)
        {
            createCallbackSubscription.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        var listCallbackSubscriptions = group.MapGet("/{authorityId:guid}/callback-subscriptions", async (
            Guid authorityId,
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

            var subscriptions = await db.CallbackSubscriptions
                .Where(x => x.AuthorityId == authority.Id)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(cancellationToken);

            return Results.Ok(new { items = subscriptions.Select(ProjectCallbackSubscription) });
        });
        if (strictAuthorityAuth)
        {
            listCallbackSubscriptions.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        var updateCallbackSubscription = group.MapPatch("/{authorityId:guid}/callback-subscriptions/{subscriptionId:guid}", async (
            Guid authorityId,
            Guid subscriptionId,
            UpdateCallbackSubscriptionRequest request,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var subscription = await db.CallbackSubscriptions.FirstOrDefaultAsync(
                x => x.Id == subscriptionId && x.AuthorityId == authority.Id,
                cancellationToken);
            if (subscription is null)
            {
                return Results.NotFound();
            }

            if (request.Enabled is not null)
            {
                subscription.Enabled = request.Enabled.Value;
            }

            if (request.SubscribedEvents is not null)
            {
                subscription.SubscribedEventsJson = JsonSerializer.Serialize(request.SubscribedEvents, JsonDefaults.SerializerOptions);
            }

            subscription.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(ProjectCallbackSubscription(subscription));
        });
        if (strictAuthorityAuth)
        {
            updateCallbackSubscription.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        var rotateCallbackSecret = group.MapPost("/{authorityId:guid}/callback-subscriptions/{subscriptionId:guid}/rotate-secret", async (
            Guid authorityId,
            Guid subscriptionId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IApiKeyHasher secretGenerator,
            IDataProtectionProvider dataProtectionProvider,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var subscription = await db.CallbackSubscriptions.FirstOrDefaultAsync(
                x => x.Id == subscriptionId && x.AuthorityId == authority.Id,
                cancellationToken);
            if (subscription is null)
            {
                return Results.NotFound();
            }

            var secret = secretGenerator.GenerateApiKey();
            subscription.Secret = dataProtectionProvider
                .CreateProtector("Sps.P2G.CallbackSecret.v1")
                .Protect(secret);
            subscription.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                subscription.Id,
                subscription.CallbackUrl,
                subscription.Enabled,
                secret
            });
        });
        if (strictAuthorityAuth)
        {
            rotateCallbackSecret.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        var listCallbackDeliveries = group.MapGet("/{authorityId:guid}/callback-deliveries", async (
            Guid authorityId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            Guid? subscriptionId,
            Guid? eventId,
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

            var normalizedPage = NormalizePage(page);
            var normalizedPageSize = NormalizePageSize(pageSize);
            var query = db.CallbackDeliveries.Where(x => x.AuthorityId == authority.Id);
            if (subscriptionId is not null)
            {
                query = query.Where(x => x.CallbackSubscriptionId == subscriptionId);
            }

            if (eventId is not null)
            {
                query = query.Where(x => x.EventId == eventId);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(x => x.Status == status);
            }

            var total = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderByDescending(x => x.CreatedAt)
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .Select(x => new
                {
                    x.Id,
                    x.EventId,
                    x.CallbackSubscriptionId,
                    x.Url,
                    x.Status,
                    x.AttemptCount,
                    x.NextAttemptAt,
                    x.LastAttemptAt,
                    x.LastHttpStatus,
                    x.LastError,
                    x.CreatedAt,
                    x.UpdatedAt
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(new { page = normalizedPage, pageSize = normalizedPageSize, total, items });
        });
        if (strictAuthorityAuth)
        {
            listCallbackDeliveries.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        var retryCallbackDelivery = group.MapPost("/{authorityId:guid}/callback-deliveries/{deliveryId:guid}/retry", async (
            Guid authorityId,
            Guid deliveryId,
            HttpContext httpContext,
            GatewayDbContext db,
            IApiKeyHasher hasher,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            var authority = await EndpointAuth.RequireAuthorityAsync(httpContext, authorityId, db, hasher, cancellationToken);
            if (authority is null)
            {
                return Results.Unauthorized();
            }

            var delivery = await db.CallbackDeliveries.FirstOrDefaultAsync(
                x => x.Id == deliveryId && x.AuthorityId == authority.Id,
                cancellationToken);
            if (delivery is null)
            {
                return Results.NotFound();
            }

            var subscription = await db.CallbackSubscriptions.FirstOrDefaultAsync(
                x => x.Id == delivery.CallbackSubscriptionId && x.AuthorityId == authority.Id,
                cancellationToken);
            if (subscription is null || !subscription.Enabled)
            {
                return Results.Conflict(new { error = "callback subscription is missing or disabled" });
            }

            var now = clock.UtcNow;
            delivery.Status = "Pending";
            delivery.AttemptCount = 0;
            delivery.NextAttemptAt = now;
            delivery.LastError = "";
            delivery.UpdatedAt = now;

            var outbox = await db.OutboxMessages.FirstOrDefaultAsync(x => x.AggregateId == delivery.Id && x.Type.StartsWith("Callback:"), cancellationToken);
            if (outbox is null)
            {
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    Type = $"Callback:{ResolveCallbackEventType(delivery.PayloadJson)}",
                    AggregateId = delivery.Id,
                    PayloadJson = delivery.PayloadJson,
                    Status = "Pending",
                    Attempts = 0,
                    NextAttemptAt = now,
                    CreatedAt = now,
                    LastError = ""
                });
            }
            else
            {
                outbox.Status = "Pending";
                outbox.Attempts = 0;
                outbox.NextAttemptAt = now;
                outbox.ProcessedAt = null;
                outbox.LastError = "";
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new
            {
                delivery.Id,
                delivery.Status,
                delivery.AttemptCount,
                delivery.NextAttemptAt
            });
        });
        if (strictAuthorityAuth)
        {
            retryCallbackDelivery.RequireAuthorization(AuthPolicies.RevenueAuthorityClient);
        }

        return app;
    }

    private static object ProjectCallbackSubscription(CallbackSubscription subscription) => new
    {
        subscription.Id,
        subscription.CallbackUrl,
        subscribedEvents = JsonSerializer.Deserialize<string[]>(subscription.SubscribedEventsJson, JsonDefaults.SerializerOptions) ?? Array.Empty<string>(),
        subscription.Enabled,
        subscription.CreatedAt,
        subscription.UpdatedAt
    };

    private static string ResolveCallbackEventType(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("eventType", out var eventType) &&
                   !string.IsNullOrWhiteSpace(eventType.GetString())
                ? eventType.GetString()!
                : "Unknown";
        }
        catch (JsonException)
        {
            return "Unknown";
        }
    }

    private static int NormalizePage(int? page) => page is null or <= 0 ? 1 : page.Value;
    private static int NormalizePageSize(int? pageSize) => pageSize is null or <= 0 or > 200 ? 50 : pageSize.Value;
}

public sealed record CreateAuthorityRequest(string Code, string Name);

public sealed record CreateRevenueAccountRequest(
    string MdaCode,
    string ServiceCode,
    string? RevenueCode,
    string? BankCode,
    string? BankName,
    string AccountNumber,
    string AccountName,
    string? ClearingType,
    string? BillerBic,
    string? Currency,
    bool IsDefault);

public sealed record CreateCallbackSubscriptionRequest(
    string CallbackUrl,
    string[]? SubscribedEvents,
    bool Enabled);

public sealed record UpdateCallbackSubscriptionRequest(
    bool? Enabled,
    string[]? SubscribedEvents);
