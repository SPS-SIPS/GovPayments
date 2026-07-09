using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.Observability;

namespace Sps.P2G.Gateway.Tests;

public sealed class CorrelationIdTests : IClassFixture<P2GWebApplicationFactory>
{
    private readonly P2GWebApplicationFactory _factory;

    public CorrelationIdTests(P2GWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Response_EchoesProvidedCorrelationId()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "corr-123");

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values));
        Assert.Equal("corr-123", Assert.Single(values));
    }

    [Fact]
    public async Task Response_GeneratesCorrelationIdWhenMissing()
    {
        var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values));
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(values)));
    }

    [Fact]
    public async Task BillCreatedEvent_StoresRequestCorrelationId()
    {
        var client = _factory.CreateClient();
        var setup = await P2GFlowTests.CreateAuthorityAccountAndBillAsync(
            client,
            invoiceId: "INV-CORR-001",
            upr: "SRA-TAX-2026-INV-CORR-001",
            billCorrelationId: "bill-corr-001");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var createdEvent = await db.BillEvents.SingleAsync(x =>
            x.BillId == setup.BillId && x.EventType == BillEventTypes.BillCreated);

        Assert.Equal("bill-corr-001", createdEvent.CorrelationId);
    }
}
