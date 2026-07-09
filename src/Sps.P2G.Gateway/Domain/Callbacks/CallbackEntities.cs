namespace Sps.P2G.Gateway.Domain.Callbacks;

public sealed class CallbackSubscription
{
    public Guid Id { get; set; }
    public Guid AuthorityId { get; set; }
    public string CallbackUrl { get; set; } = "";
    public string Secret { get; set; } = "";
    public string SubscribedEventsJson { get; set; } = "[]";
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CallbackDelivery
{
    public Guid Id { get; set; }
    public Guid AuthorityId { get; set; }
    public Guid EventId { get; set; }
    public Guid CallbackSubscriptionId { get; set; }
    public string Url { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastHttpStatus { get; set; }
    public string LastError { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public Guid AggregateId { get; set; }
    public string PayloadJson { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string LastError { get; set; } = "";
}
