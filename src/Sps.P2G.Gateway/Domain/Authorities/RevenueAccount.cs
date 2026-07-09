namespace Sps.P2G.Gateway.Domain.Authorities;

public sealed class RevenueAccount
{
    public Guid Id { get; set; }
    public Guid AuthorityId { get; set; }
    public string MdaCode { get; set; } = "";
    public string MdaName { get; set; } = "";
    public string ServiceCode { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string RevenueCode { get; set; } = "";
    public string BankCode { get; set; } = "";
    public string BankName { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string ClearingType { get; set; } = "ACCT";
    public string BillerBic { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public bool IsDefault { get; set; }
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
