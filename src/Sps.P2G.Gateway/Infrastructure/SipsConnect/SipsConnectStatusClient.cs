using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sps.P2G.Gateway.Infrastructure.SipsConnect;

public interface ISipsConnectStatusClient
{
    Task<SipsConnectStatusResult> CheckPaymentStatusAsync(SipsConnectStatusRequest request, CancellationToken cancellationToken);
}

public sealed record SipsConnectStatusRequest(
    string? TxId,
    string? EndToEndId,
    string? Agent);

public sealed record SipsConnectStatusResult(
    HttpStatusCode StatusCode,
    string ResponseBody);

public sealed class SipsConnectStatusClient(
    IHttpClientFactory httpClientFactory,
    IOptions<SipsConnectClientOptions> optionsAccessor) : ISipsConnectStatusClient
{
    public async Task<SipsConnectStatusResult> CheckPaymentStatusAsync(SipsConnectStatusRequest request, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled)
        {
            throw new InvalidOperationException("SipsConnectClient is not enabled.");
        }

        using var client = httpClientFactory.CreateClient("SipsConnect");
        var accessToken = await GetAccessTokenAsync(client, options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await client.PostAsJsonAsync(
            options.PaymentStatusPath.TrimStart('/'),
            request,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new SipsConnectStatusResult(response.StatusCode, body);
    }

    private static async Task<string> GetAccessTokenAsync(HttpClient client, SipsConnectClientOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.TokenEndpoint) ||
            string.IsNullOrWhiteSpace(options.ClientId) ||
            string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return "";
        }

        var formValues = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", options.ClientId),
            new("client_secret", options.ClientSecret)
        };

        if (!string.IsNullOrWhiteSpace(options.Scope))
        {
            formValues.Add(new KeyValuePair<string, string>("scope", options.Scope));
        }

        using var tokenResponse = await client.PostAsync(options.TokenEndpoint, new FormUrlEncodedContent(formValues), cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();

        await using var stream = await tokenResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString() ?? ""
            : "";
    }
}
