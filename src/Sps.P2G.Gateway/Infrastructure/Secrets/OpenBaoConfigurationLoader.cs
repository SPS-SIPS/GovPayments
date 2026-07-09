using System.Net.Http.Headers;
using System.Text.Json;

namespace Sps.P2G.Gateway.Infrastructure.Secrets;

public static class OpenBaoConfigurationLoader
{
    public static async Task AddSecretsAsync(IConfigurationManager configuration)
    {
        var options = configuration.GetSection("OpenBao").Get<OpenBaoOptions>() ?? new OpenBaoOptions();
        if (!options.Enabled)
        {
            return;
        }

        ValidateOptions(options);

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 60))
        };
        httpClient.DefaultRequestHeaders.Add("X-Vault-Token", options.Token);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var requestPath = $"v1/{TrimSlashes(options.KvMount)}/data/{TrimSlashes(options.SecretPath)}";
        using var response = await httpClient.GetAsync(requestPath);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"OpenBao secret load failed for {options.KvMount}/{options.SecretPath}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var values = ExtractConfiguration(document);
        if (values.Count == 0)
        {
            throw new InvalidOperationException($"OpenBao secret {options.KvMount}/{options.SecretPath} did not contain configuration values.");
        }

        configuration.AddInMemoryCollection(values);
    }

    public static Dictionary<string, string?> ExtractConfiguration(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("data", out var outerData) ||
            !outerData.TryGetProperty("data", out var secretData) ||
            secretData.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Flatten(secretData, "", values);
        return values;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> values)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = string.IsNullOrWhiteSpace(prefix)
                ? NormalizeKey(property.Name)
                : $"{prefix}:{NormalizeKey(property.Name)}";

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, key, values);
                    break;
                case JsonValueKind.Array:
                    values[key] = property.Value.GetRawText();
                    break;
                case JsonValueKind.String:
                    values[key] = property.Value.GetString();
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    values[key] = property.Value.ToString();
                    break;
                case JsonValueKind.Null:
                    values[key] = null;
                    break;
            }
        }
    }

    private static void ValidateOptions(OpenBaoOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException("OpenBao:BaseUrl is required when OpenBao is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("OpenBao:Token is required when OpenBao is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.KvMount))
        {
            throw new InvalidOperationException("OpenBao:KvMount is required when OpenBao is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.SecretPath))
        {
            throw new InvalidOperationException("OpenBao:SecretPath is required when OpenBao is enabled.");
        }
    }

    private static string NormalizeKey(string key) => key.Replace("__", ":", StringComparison.Ordinal);

    private static string TrimSlashes(string value) => value.Trim().Trim('/');
}
