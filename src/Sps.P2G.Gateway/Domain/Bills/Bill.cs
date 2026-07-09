namespace Sps.P2G.Gateway.Domain.Bills;

public sealed class Bill
{
    public Guid Id { get; set; }
    public Guid AuthorityId { get; set; }
    public string ExternalInvoiceId { get; set; } = "";
    public string UPR { get; set; } = "";
    public string MdaCode { get; set; } = "";
    public string ServiceCode { get; set; } = "";
    public string RevenueCode { get; set; } = "";
    public string TaxpayerReference { get; set; } = "";
    public string PayerDisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateOnly? DueDate { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid RevenueAccountId { get; set; }
    public string StatusProjection { get; set; } = BillStatuses.Available;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string CreatedPayloadJson { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string RecordHash { get; set; } = "";
}

public static class BillStatuses
{
    public const string Available = "Available";
    public const string Fetched = "Fetched";
    public const string PaymentInitiated = "PaymentInitiated";
    public const string Paid = "Paid";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
    public const string Reversed = "Reversed";
    public const string PartiallyReversed = "PartiallyReversed";
    public const string Failed = "Failed";
}

public static class BillEventTypes
{
    public const string BillCreated = "BillCreated";
    public const string BillFetched = "BillFetched";
    public const string VerificationReturned = "VerificationReturned";
    public const string PaymentAccepted = "PaymentAccepted";
    public const string PaymentRejected = "PaymentRejected";
    public const string BillPaid = "BillPaid";
    public const string BillCancelled = "BillCancelled";
    public const string BillReversed = "BillReversed";
    public const string CallbackQueued = "CallbackQueued";
}
