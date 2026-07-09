using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Tests;

public sealed class StrictJwtWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://issuer.test/realms/sps";
    public const string Audience = "sps-p2g-gateway";
    public const string SigningKey = "strict-jwt-test-signing-key-32bytes";

    private readonly string _databaseName = $"strict-jwt-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:P2G"] = "Host=unused",
                ["P2G:DescriptorHmacSecret"] = "test-descriptor-secret",
                ["P2G:InternalApiKey"] = "test-internal-key",
                ["Callbacks:Enabled"] = "false",
                ["Keycloak:Enabled"] = "true",
                ["Keycloak:Authority"] = "",
                ["Keycloak:ValidIssuer"] = Issuer,
                ["Keycloak:SymmetricSigningKey"] = SigningKey,
                ["Keycloak:Audience"] = Audience,
                ["Keycloak:RequireHttpsMetadata"] = "false",
                ["Keycloak:RevenueAuthorityRole"] = "p2g-authority",
                ["Keycloak:SipsConnectRole"] = "p2g-sips-connect",
                ["Keycloak:DashboardRole"] = "p2g-dashboard",
                ["Keycloak:SipsConnectClientId"] = "sips-connect-p2g",
                ["Auth:AllowAuthorityApiKeyFallback"] = "false",
                ["Auth:AllowSipsConnectInternalKeyFallback"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<GatewayDbContext>>();
            services.AddDbContext<GatewayDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
        });
    }
}
