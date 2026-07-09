namespace Sps.P2G.Gateway.Infrastructure.Security;

public sealed class KeycloakOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string MetadataAddress { get; set; } = "";
    public string ValidIssuer { get; set; } = "";
    public string SymmetricSigningKey { get; set; } = "";
    public string Audience { get; set; } = "sps-p2g-gateway";
    public bool RequireHttpsMetadata { get; set; } = true;
    public string RevenueAuthorityRole { get; set; } = "p2g-authority";
    public string SipsConnectRole { get; set; } = "p2g-sips-connect";
    public string DashboardRole { get; set; } = "p2g-dashboard";
    public string SipsConnectClientId { get; set; } = "sips-connect-p2g";
}
