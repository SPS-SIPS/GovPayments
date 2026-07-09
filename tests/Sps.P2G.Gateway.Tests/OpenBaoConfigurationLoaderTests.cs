using System.Text.Json;
using Sps.P2G.Gateway.Infrastructure.Secrets;

namespace Sps.P2G.Gateway.Tests;

public sealed class OpenBaoConfigurationLoaderTests
{
    [Fact]
    public void ExtractConfiguration_MapsKvV2PayloadToAspNetConfigurationKeys()
    {
        using var document = JsonDocument.Parse("""
        {
          "data": {
            "data": {
              "ConnectionStrings__P2G": "Host=postgres;Database=p2g",
              "P2G": {
                "DescriptorHmacSecret": "secret-value"
              },
              "Keycloak__RequireHttpsMetadata": true,
              "Callbacks__BatchSize": 50
            }
          }
        }
        """);

        var values = OpenBaoConfigurationLoader.ExtractConfiguration(document);

        Assert.Equal("Host=postgres;Database=p2g", values["ConnectionStrings:P2G"]);
        Assert.Equal("secret-value", values["P2G:DescriptorHmacSecret"]);
        Assert.Equal("True", values["Keycloak:RequireHttpsMetadata"]);
        Assert.Equal("50", values["Callbacks:BatchSize"]);
    }
}
