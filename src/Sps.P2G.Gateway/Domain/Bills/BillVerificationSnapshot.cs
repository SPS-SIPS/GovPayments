namespace Sps.P2G.Gateway.Domain.Bills;

public sealed class BillVerificationSnapshot
{
    public Guid Id { get; set; }
    public Guid BillId { get; set; }
    public Guid AuthorityId { get; set; }
    public string VerificationRequestId { get; set; } = "";
    public string PayerBankCode { get; set; } = "";
    public string PayerChannel { get; set; } = "";
    public string Descriptor { get; set; } = "";
    public string DescriptorVersion { get; set; } = "P2G1";
    public string DescriptorHmac { get; set; } = "";
    public int DescriptorKeyVersion { get; set; } = 1;
    public string CreditorClearingId { get; set; } = "";
    public string CreditorClearingType { get; set; } = "";
    public string CreditorName { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
