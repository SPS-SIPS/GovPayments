namespace Sps.P2G.Gateway.Domain.Authorities;

public sealed class RevenueAuthority
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Active";
    public string ApiKeyHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
