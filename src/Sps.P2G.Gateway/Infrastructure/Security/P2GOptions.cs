using System.ComponentModel.DataAnnotations;

namespace Sps.P2G.Gateway.Infrastructure.Security;

public sealed class P2GOptions
{
    [Required]
    public string DescriptorHmacSecret { get; set; } = "development-descriptor-secret-change-me";

    public string InternalApiKey { get; set; } = "development-internal-key-change-me";
}
