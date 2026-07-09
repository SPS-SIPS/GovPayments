using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Tests;

public class P2GWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"p2g-tests-{Guid.NewGuid()}";

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
                ["Callbacks:Enabled"] = "false"
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
