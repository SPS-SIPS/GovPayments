using System.Security.Claims;
using Sps.P2G.Gateway.Infrastructure.Security;

namespace Sps.P2G.Gateway.Tests;

public sealed class AuthClaimReaderTests
{
    [Fact]
    public void GetClientId_ReadsKeycloakAuthorizedPartyClaim()
    {
        var user = Principal(
            new Claim("azp", "SRA"),
            new Claim("client_id", "ignored"));

        Assert.Equal("SRA", AuthClaimReader.GetClientId(user));
    }

    [Fact]
    public void HasRealmRole_ReadsKeycloakRealmAccessRoles()
    {
        var user = Principal(new Claim("realm_access", """{"roles":["p2g-authority","offline_access"]}"""));

        Assert.True(AuthClaimReader.HasRealmRole(user, "p2g-authority"));
        Assert.False(AuthClaimReader.HasRealmRole(user, "p2g-dashboard"));
    }

    [Fact]
    public void HasClientRole_ReadsKeycloakResourceAccessRoles()
    {
        var user = Principal(new Claim("resource_access", """
        {
          "sps-p2g-gateway": { "roles": ["p2g-authority"] },
          "sips-connect-p2g": { "roles": ["p2g-sips-connect"] }
        }
        """));

        Assert.True(AuthClaimReader.HasClientRole(user, "p2g-sips-connect", "sips-connect-p2g"));
        Assert.False(AuthClaimReader.HasClientRole(user, "p2g-sips-connect", "sps-p2g-gateway"));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        return new ClaimsPrincipal(identity);
    }
}
