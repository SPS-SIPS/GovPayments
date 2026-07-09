namespace Sps.P2G.Gateway.Domain.Bills;

public sealed class BillEvent
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public Guid BillId { get; set; }
    public Guid AuthorityId { get; set; }
    public string EventType { get; set; } = "";
    public DateTimeOffset EventTime { get; set; }
    public string ActorType { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string SourceSystem { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string PreviousEventHash { get; set; } = "";
    public string EventHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
