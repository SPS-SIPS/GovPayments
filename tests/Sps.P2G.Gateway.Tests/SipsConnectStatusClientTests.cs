using System.Net;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Infrastructure.SipsConnect;

namespace Sps.P2G.Gateway.Tests;

public sealed class SipsConnectStatusClientTests
{
    [Fact]
    public async Task CheckPaymentStatusAsync_UsesClientCredentialsTokenAndPostsStatusRequest()
    {
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/realms/sips/protocol/openid-connect/token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"uat-token"}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"ACSC"}""")
            };
        });
        var client = new SipsConnectStatusClient(
            new FakeHttpClientFactory(handler),
            Options.Create(new SipsConnectClientOptions
            {
                Enabled = true,
                BaseUrl = "https://sips-connect-uat.example",
                TokenEndpoint = "https://keycloak.example/realms/sips/protocol/openid-connect/token",
                ClientId = "sps-p2g-gateway",
                ClientSecret = "secret",
                Scope = "payments:status",
                PaymentStatusPath = "/api/v1/payments/status"
            }));

        var result = await client.CheckPaymentStatusAsync(new SipsConnectStatusRequest("TX-1", "E2E-1", "SPSP"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("ACSC", result.ResponseBody, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/realms/sips/protocol/openid-connect/token", handler.Requests[0].RequestUri?.AbsolutePath);
        Assert.Equal("/api/v1/payments/status", handler.Requests[1].RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", handler.Requests[1].Headers.Authorization?.Scheme);
        Assert.Equal("uat-token", handler.Requests[1].Headers.Authorization?.Parameter);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://sips-connect-uat.example")
        };
    }
}
