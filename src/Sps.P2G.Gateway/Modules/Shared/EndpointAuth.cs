using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Domain.Authorities;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.Security;

namespace Sps.P2G.Gateway.Modules.Shared;

public static class EndpointAuth
{
    public static async Task<RevenueAuthority?> RequireAuthorityAsync(
        HttpContext httpContext,
        Guid authorityId,
        GatewayDbContext db,
        IApiKeyHasher hasher,
        CancellationToken cancellationToken)
    {
        var tokenAuthority = await ResolveAuthorityFromTokenAsync(httpContext, authorityId, db, cancellationToken);
        if (tokenAuthority is not null)
        {
            return tokenAuthority;
        }

        var authOptions = httpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!authOptions.AllowAuthorityApiKeyFallback)
        {
            return null;
        }

        var apiKey = httpContext.Request.Headers["X-Authority-Api-Key"].FirstOrDefault()
            ?? httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var authority = await db.RevenueAuthorities.FirstOrDefaultAsync(x => x.Id == authorityId, cancellationToken);
        if (authority is null || !string.Equals(authority.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return hasher.Verify(apiKey, authority.ApiKeyHash) ? authority : null;
    }

    public static async Task<RevenueAuthority?> ResolveAuthorityFromTokenAsync(
        HttpContext httpContext,
        Guid authorityId,
        GatewayDbContext db,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var clientId = AuthClaimReader.GetClientId(user);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var keycloakOptions = httpContext.RequestServices.GetRequiredService<IOptions<KeycloakOptions>>().Value;
        if (keycloakOptions.Enabled &&
            !AuthClaimReader.HasRealmRole(user, keycloakOptions.RevenueAuthorityRole) &&
            !AuthClaimReader.HasClientRole(user, keycloakOptions.RevenueAuthorityRole, clientId))
        {
            return null;
        }

        var authority = await db.RevenueAuthorities.FirstOrDefaultAsync(
            x => x.Id == authorityId && x.Code == clientId.ToUpper() && x.Status == "Active",
            cancellationToken);
        return authority;
    }

    public static bool HasInternalAccess(HttpContext httpContext, IOptions<P2GOptions> options)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            var keycloakOptions = httpContext.RequestServices.GetRequiredService<IOptions<KeycloakOptions>>().Value;
            var clientId = AuthClaimReader.GetClientId(httpContext.User);
            if (!string.IsNullOrWhiteSpace(clientId) &&
                string.Equals(clientId, keycloakOptions.SipsConnectClientId, StringComparison.OrdinalIgnoreCase) &&
                (AuthClaimReader.HasRealmRole(httpContext.User, keycloakOptions.SipsConnectRole) ||
                 AuthClaimReader.HasClientRole(httpContext.User, keycloakOptions.SipsConnectRole, clientId)))
            {
                return true;
            }
        }

        var authOptions = httpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!authOptions.AllowSipsConnectInternalKeyFallback)
        {
            return false;
        }

        var configuredKey = options.Value.InternalApiKey;
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return true;
        }

        var suppliedKey = httpContext.Request.Headers["X-Internal-Api-Key"].FirstOrDefault();
        return string.Equals(suppliedKey, configuredKey, StringComparison.Ordinal);
    }
}
