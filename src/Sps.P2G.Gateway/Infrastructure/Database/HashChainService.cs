using Microsoft.EntityFrameworkCore;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Infrastructure.Time;

namespace Sps.P2G.Gateway.Infrastructure.Database;

public interface IHashChainService
{
    Task<BillEvent> AppendAsync(
        GatewayDbContext db,
        Bill bill,
        string eventType,
        object payload,
        string actorType,
        string actorId,
        string sourceSystem,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class HashChainService(IClock clock) : IHashChainService
{
    public async Task<BillEvent> AppendAsync(
        GatewayDbContext db,
        Bill bill,
        string eventType,
        object payload,
        string actorType,
        string actorId,
        string sourceSystem,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var payloadJson = JsonDefaults.Serialize(payload);
        var localPreviousEvent = db.ChangeTracker
            .Entries<BillEvent>()
            .Where(x => x.State == EntityState.Added && x.Entity.BillId == bill.Id)
            .Select(x => x.Entity)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefault();
        var previousHash = localPreviousEvent?.EventHash ??
            await db.BillEvents
                .Where(x => x.BillId == bill.Id)
                .OrderByDescending(x => x.Sequence)
                .Select(x => x.EventHash)
                .FirstOrDefaultAsync(cancellationToken) ?? bill.RecordHash;
        var nextSequence = await GetNextSequenceAsync(db, cancellationToken);

        var payloadHash = JsonDefaults.Sha256(payloadJson);
        var eventHash = JsonDefaults.Sha256($"{bill.Id}|{eventType}|{now:O}|{payloadHash}|{previousHash}");
        var evt = new BillEvent
        {
            Id = Guid.NewGuid(),
            Sequence = nextSequence,
            BillId = bill.Id,
            AuthorityId = bill.AuthorityId,
            EventType = eventType,
            EventTime = now,
            ActorType = actorType,
            ActorId = actorId,
            SourceSystem = sourceSystem,
            CorrelationId = correlationId,
            PayloadJson = payloadJson,
            PayloadHash = payloadHash,
            PreviousEventHash = previousHash,
            EventHash = eventHash,
            CreatedAt = now
        };

        db.BillEvents.Add(evt);
        return evt;
    }

    private static async Task<long> GetNextSequenceAsync(GatewayDbContext db, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            return await db.Database
                .SqlQueryRaw<long>("SELECT nextval('\"BillEventSequence\"') AS \"Value\"")
                .SingleAsync(cancellationToken);
        }

        var persistedMaxSequence = await db.BillEvents
            .Select(x => (long?)x.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        var localMaxSequence = db.ChangeTracker
            .Entries<BillEvent>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => (long?)x.Entity.Sequence)
            .Max() ?? 0;
        return Math.Max(persistedMaxSequence, localMaxSequence) + 1;
    }
}
