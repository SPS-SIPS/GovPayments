using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Sps.P2G.Gateway.Tests;

public sealed class RequestProtectedWebApplicationFactory : P2GWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RequestProtection:Enabled"] = "true",
                ["RequestProtection:MaxRequestBodyBytes"] = "32",
                ["RateLimits:Enabled"] = "false"
            });
        });
    }
}
