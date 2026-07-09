using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Sps.P2G.Gateway.Tests;

public sealed class RateLimitedWebApplicationFactory : P2GWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:Enabled"] = "true",
                ["RateLimits:AuthorityPermitLimit"] = "1",
                ["RateLimits:SipsConnectPermitLimit"] = "1",
                ["RateLimits:DashboardPermitLimit"] = "1",
                ["RateLimits:WindowSeconds"] = "60",
                ["RateLimits:QueueLimit"] = "0"
            });
        });
    }
}
