namespace Sps.P2G.Gateway.Domain.Payments;

public sealed class BillPayment
{
    public Guid Id { get; set; }
    public Guid BillId { get; set; }
    public Guid AuthorityId { get; set; }
    public string SipsTransactionId { get; set; } = "";
    public string EndToEndId { get; set; } = "";
    public string OriginalMessageId { get; set; } = "";
    public string TxId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset PaidAt { get; set; }
    public string PayerName { get; set; } = "";
    public string PayerAccount { get; set; } = "";
    public string PayerBankCode { get; set; } = "";
    public string PayerBankName { get; set; } = "";
    public string PayerClearingType { get; set; } = "";
    public string PayeeName { get; set; } = "";
    public string PayeeAccount { get; set; } = "";
    public string PayeeBankCode { get; set; } = "";
    public string PayeeBankName { get; set; } = "";
    public string PayeeClearingType { get; set; } = "";
    public string RawPayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BillPaymentAttempt
{
    public Guid Id { get; set; }
    public Guid? BillId { get; set; }
    public Guid? AuthorityId { get; set; }
    public string TxId { get; set; } = "";
    public string EndToEndId { get; set; } = "";
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public string RawPayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BillReversal
{
    public Guid Id { get; set; }
    public Guid BillId { get; set; }
    public Guid AuthorityId { get; set; }
    public string OriginalSipsTransactionId { get; set; } = "";
    public string ReversalTransactionId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset ReversedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
