using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Sps.P2G.Gateway.Infrastructure.Configuration;

namespace Sps.P2G.Gateway.Tests;

public sealed class ProductionConfigurationValidatorTests
{
    [Fact]
    public void Validate_SkipsTestingEnvironment()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Record.Exception(() =>
            ProductionConfigurationValidator.Validate(new TestEnvironment("Testing"), configuration));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_RejectsUnsafeProductionDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:P2G"] = "",
                ["P2G:DescriptorHmacSecret"] = "development-descriptor-secret-change-me",
                ["P2G:InternalApiKey"] = "development-internal-key-change-me",
                ["Auth:AllowAuthorityApiKeyFallback"] = "true",
                ["Auth:AllowSipsConnectInternalKeyFallback"] = "true",
                ["CallbackSecurity:RequireHttps"] = "false",
                ["CallbackSecurity:AllowPrivateNetworkTargets"] = "true",
                ["Keycloak:Enabled"] = "false",
                ["Keycloak:RequireHttpsMetadata"] = "false",
                ["OpenBao:Enabled"] = "false",
                ["DataProtection:PersistKeysToFileSystemPath"] = ""
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(new TestEnvironment("Production"), configuration));

        Assert.Contains("ConnectionStrings:P2G is required", exception.Message);
        Assert.Contains("Keycloak:Enabled must be true", exception.Message);
        Assert.Contains("AllowAuthorityApiKeyFallback must be false", exception.Message);
        Assert.Contains("CallbackSecurity:RequireHttps must be true", exception.Message);
        Assert.Contains("CallbackSecurity:AllowPrivateNetworkTargets must be false", exception.Message);
        Assert.Contains("OpenBao:Enabled must be true", exception.Message);
        Assert.Contains("DataProtection:PersistKeysToFileSystemPath is required", exception.Message);
    }

    [Fact]
    public void Validate_AcceptsHardenedProductionConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:P2G"] = "Host=postgres;Database=p2g;Username=p2g;Password=secret",
                ["P2G:DescriptorHmacSecret"] = "production-descriptor-secret-32-bytes-minimum",
                ["P2G:InternalApiKey"] = "",
                ["Auth:AllowAuthorityApiKeyFallback"] = "false",
                ["Auth:AllowSipsConnectInternalKeyFallback"] = "false",
                ["CallbackSecurity:RequireHttps"] = "true",
                ["CallbackSecurity:AllowPrivateNetworkTargets"] = "false",
                ["Keycloak:Enabled"] = "true",
                ["Keycloak:Authority"] = "https://keycloak.example/realms/sps",
                ["Keycloak:Audience"] = "sps-p2g-gateway",
                ["Keycloak:RequireHttpsMetadata"] = "true",
                ["Keycloak:RevenueAuthorityRole"] = "p2g-authority",
                ["Keycloak:SipsConnectRole"] = "p2g-sips-connect",
                ["Keycloak:DashboardRole"] = "p2g-dashboard",
                ["Keycloak:ConfigurationAdminRole"] = "p2g-configuration-admin",
                ["Keycloak:SipsConnectClientId"] = "sips-connect-p2g",
                ["OpenBao:Enabled"] = "true",
                ["OpenBao:BaseUrl"] = "https://openbao.example",
                ["OpenBao:Token"] = "bao-token",
                ["OpenBao:KvMount"] = "secret",
                ["OpenBao:SecretPath"] = "sps-p2g-gateway/runtime",
                ["DataProtection:PersistKeysToFileSystemPath"] = "/var/lib/sps-p2g-gateway/data-protection-keys"
            })
            .Build();

        var exception = Record.Exception(() =>
            ProductionConfigurationValidator.Validate(new TestEnvironment("Production"), configuration));

        Assert.Null(exception);
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Sps.P2G.Gateway.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
