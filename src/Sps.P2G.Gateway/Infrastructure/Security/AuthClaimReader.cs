using System.Security.Claims;
using System.Text.Json;

namespace Sps.P2G.Gateway.Infrastructure.Security;

public static class AuthClaimReader
{
    public static string? GetClientId(ClaimsPrincipal user)
    {
        return FirstClaim(user, "azp", "client_id", "clientId", "appid");
    }

    public static bool HasRealmRole(ClaimsPrincipal user, string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        if (user.Claims.Any(x => IsRoleClaim(x.Type) && string.Equals(x.Value, role, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var realmAccess = user.FindFirst("realm_access")?.Value;
        return JsonContainsRole(realmAccess, role);
    }

    public static bool HasClientRole(ClaimsPrincipal user, string role, string? clientId = null)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        var resourceAccess = user.FindFirst("resource_access")?.Value;
        if (string.IsNullOrWhiteSpace(resourceAccess))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(resourceAccess);
            foreach (var client in document.RootElement.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(clientId) &&
                    !string.Equals(client.Name, clientId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (client.Value.TryGetProperty("roles", out var roles) &&
                    roles.ValueKind == JsonValueKind.Array &&
                    roles.EnumerateArray().Any(x => string.Equals(x.GetString(), role, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string? FirstClaim(ClaimsPrincipal user, params string[] claimTypes)
    {
        return claimTypes
            .Select(type => user.FindFirst(type)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsRoleClaim(string claimType)
    {
        return claimType is ClaimTypes.Role or "role" or "roles";
    }

    private static bool JsonContainsRole(string? json, string role)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("roles", out var roles) &&
                   roles.ValueKind == JsonValueKind.Array &&
                   roles.EnumerateArray().Any(x => string.Equals(x.GetString(), role, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
