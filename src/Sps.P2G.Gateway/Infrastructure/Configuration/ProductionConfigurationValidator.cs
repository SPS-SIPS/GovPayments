using Sps.P2G.Gateway.Infrastructure.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Secrets;
using Sps.P2G.Gateway.Infrastructure.Security;

namespace Sps.P2G.Gateway.Infrastructure.Configuration;

public static class ProductionConfigurationValidator
{
    private const string DevelopmentDescriptorSecret = "development-descriptor-secret-change-me";
    private const string DevelopmentInternalApiKey = "development-internal-key-change-me";

    public static void Validate(IHostEnvironment environment, IConfiguration configuration)
    {
        if (environment.IsDevelopment() ||
            environment.IsEnvironment("Testing") ||
            environment.IsEnvironment("IntegrationTesting"))
        {
            return;
        }

        var errors = new List<string>();
        var connectionString = configuration.GetConnectionString("P2G");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add("ConnectionStrings:P2G is required.");
        }

        var p2g = configuration.GetSection("P2G").Get<P2GOptions>() ?? new P2GOptions();
        if (string.IsNullOrWhiteSpace(p2g.DescriptorHmacSecret) ||
            p2g.DescriptorHmacSecret == DevelopmentDescriptorSecret ||
            p2g.DescriptorHmacSecret.Length < 32)
        {
            errors.Add("P2G:DescriptorHmacSecret must be a non-default secret of at least 32 characters.");
        }

        var auth = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        if (auth.AllowAuthorityApiKeyFallback)
        {
            errors.Add("Auth:AllowAuthorityApiKeyFallback must be false outside development and test environments.");
        }

        if (auth.AllowSipsConnectInternalKeyFallback)
        {
            errors.Add("Auth:AllowSipsConnectInternalKeyFallback must be false outside development and test environments.");
        }

        var callbackSecurity = configuration.GetSection("CallbackSecurity").Get<CallbackSecurityOptions>() ?? new CallbackSecurityOptions();
        if (!callbackSecurity.RequireHttps)
        {
            errors.Add("CallbackSecurity:RequireHttps must be true outside development and test environments.");
        }

        if (callbackSecurity.AllowPrivateNetworkTargets)
        {
            errors.Add("CallbackSecurity:AllowPrivateNetworkTargets must be false outside development and test environments.");
        }

        if (!string.IsNullOrWhiteSpace(p2g.InternalApiKey) &&
            p2g.InternalApiKey == DevelopmentInternalApiKey)
        {
            errors.Add("P2G:InternalApiKey must not use the development default.");
        }

        var keycloak = configuration.GetSection("Keycloak").Get<KeycloakOptions>() ?? new KeycloakOptions();
        if (!keycloak.Enabled)
        {
            errors.Add("Keycloak:Enabled must be true outside development and test environments.");
        }

        if (string.IsNullOrWhiteSpace(keycloak.Authority) && string.IsNullOrWhiteSpace(keycloak.MetadataAddress))
        {
            errors.Add("Keycloak:Authority or Keycloak:MetadataAddress is required.");
        }

        if (string.IsNullOrWhiteSpace(keycloak.Audience))
        {
            errors.Add("Keycloak:Audience is required.");
        }

        if (string.IsNullOrWhiteSpace(keycloak.RevenueAuthorityRole))
        {
            errors.Add("Keycloak:RevenueAuthorityRole is required.");
        }

        if (string.IsNullOrWhiteSpace(keycloak.SipsConnectRole))
        {
            errors.Add("Keycloak:SipsConnectRole is required.");
        }

        if (string.IsNullOrWhiteSpace(keycloak.DashboardRole))
        {
            errors.Add("Keycloak:DashboardRole is required.");
        }

        if (string.IsNullOrWhiteSpace(keycloak.ConfigurationAdminRole))
        {
            errors.Add("Keycloak:ConfigurationAdminRole is required.");
        }

        if (string.IsNullOrWhiteSpace(keycloak.SipsConnectClientId))
        {
            errors.Add("Keycloak:SipsConnectClientId is required.");
        }

        if (!keycloak.RequireHttpsMetadata)
        {
            errors.Add("Keycloak:RequireHttpsMetadata must be true outside development and test environments.");
        }

        var openBao = configuration.GetSection("OpenBao").Get<OpenBaoOptions>() ?? new OpenBaoOptions();
        if (!openBao.Enabled)
        {
            errors.Add("OpenBao:Enabled must be true outside development and test environments.");
        }

        if (openBao.Enabled)
        {
            if (string.IsNullOrWhiteSpace(openBao.BaseUrl))
            {
                errors.Add("OpenBao:BaseUrl is required when OpenBao is enabled.");
            }

            if (string.IsNullOrWhiteSpace(openBao.Token) && string.IsNullOrWhiteSpace(openBao.TokenFile))
            {
                errors.Add("OpenBao:Token or OpenBao:TokenFile is required when OpenBao is enabled.");
            }

            if (string.IsNullOrWhiteSpace(openBao.KvMount))
            {
                errors.Add("OpenBao:KvMount is required when OpenBao is enabled.");
            }

            if (string.IsNullOrWhiteSpace(openBao.SecretPath))
            {
                errors.Add("OpenBao:SecretPath is required when OpenBao is enabled.");
            }
        }

        var dataProtection = configuration.GetSection("DataProtection").Get<PersistentDataProtectionOptions>() ?? new PersistentDataProtectionOptions();
        if (string.IsNullOrWhiteSpace(dataProtection.PersistKeysToFileSystemPath))
        {
            errors.Add("DataProtection:PersistKeysToFileSystemPath is required outside development and test environments.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid production configuration: " + string.Join(" ", errors));
        }
    }
}
