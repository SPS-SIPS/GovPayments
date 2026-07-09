namespace Sps.P2G.Gateway.Domain.Security;

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public Guid? ResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class HmacKeyVersion
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public string KeyHash { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAt { get; set; }
}
