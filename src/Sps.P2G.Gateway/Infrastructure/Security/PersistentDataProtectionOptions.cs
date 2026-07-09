namespace Sps.P2G.Gateway.Infrastructure.Security;

public sealed class PersistentDataProtectionOptions
{
    public string ApplicationName { get; set; } = "sps-p2g-gateway";
    public string PersistKeysToFileSystemPath { get; set; } = "";
}
