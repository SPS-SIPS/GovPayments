using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Sps.P2G.Gateway.Tests;

public static class JwtTokenFactory
{
    public static string Create(
        string clientId,
        string[] realmRoles,
        string issuer = StrictJwtWebApplicationFactory.Issuer,
        string audience = StrictJwtWebApplicationFactory.Audience,
        string signingKey = StrictJwtWebApplicationFactory.SigningKey)
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = now.UtcDateTime.AddMinutes(-1),
            Expires = now.UtcDateTime.AddMinutes(15),
            Subject = new ClaimsIdentity([
                new Claim("azp", clientId),
                new Claim("client_id", clientId),
                new Claim("realm_access", $$"""{"roles":[{{string.Join(',', realmRoles.Select(role => $"\"{role}\""))}}]}""")
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
