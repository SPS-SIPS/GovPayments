using Microsoft.EntityFrameworkCore;
using Sps.P2G.Gateway.Domain.Authorities;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Domain.Callbacks;
using Sps.P2G.Gateway.Domain.Payments;
using Sps.P2G.Gateway.Domain.Security;

namespace Sps.P2G.Gateway.Infrastructure.Database;

public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    public DbSet<RevenueAuthority> RevenueAuthorities => Set<RevenueAuthority>();
    public DbSet<RevenueAccount> RevenueAccounts => Set<RevenueAccount>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<BillEvent> BillEvents => Set<BillEvent>();
    public DbSet<BillVerificationSnapshot> BillVerificationSnapshots => Set<BillVerificationSnapshot>();
    public DbSet<BillPayment> BillPayments => Set<BillPayment>();
    public DbSet<BillPaymentAttempt> BillPaymentAttempts => Set<BillPaymentAttempt>();
    public DbSet<BillReversal> BillReversals => Set<BillReversal>();
    public DbSet<CallbackSubscription> CallbackSubscriptions => Set<CallbackSubscription>();
    public DbSet<CallbackDelivery> CallbackDeliveries => Set<CallbackDelivery>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<HmacKeyVersion> HmacKeyVersions => Set<HmacKeyVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RevenueAuthority>(entity =>
        {
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(32);
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.ApiKeyHash).HasMaxLength(128);
        });

        modelBuilder.Entity<RevenueAccount>(entity =>
        {
            entity.HasIndex(x => new { x.AuthorityId, x.MdaCode, x.ServiceCode, x.IsDefault });
            entity.Property(x => x.MdaCode).HasMaxLength(32);
            entity.Property(x => x.MdaName).HasMaxLength(256);
            entity.Property(x => x.ServiceCode).HasMaxLength(32);
            entity.Property(x => x.ServiceName).HasMaxLength(256);
            entity.Property(x => x.RevenueCode).HasMaxLength(64);
            entity.Property(x => x.AccountNumber).HasMaxLength(64);
            entity.Property(x => x.AccountName).HasMaxLength(256);
            entity.Property(x => x.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<Bill>(entity =>
        {
            entity.HasIndex(x => new { x.AuthorityId, x.UPR }).IsUnique();
            entity.HasIndex(x => new { x.AuthorityId, x.ExternalInvoiceId }).IsUnique();
            entity.HasIndex(x => new { x.AuthorityId, x.CreatedAt });
            entity.HasIndex(x => new { x.AuthorityId, x.StatusProjection, x.UpdatedAt });
            entity.HasIndex(x => x.StatusProjection);
            entity.HasIndex(x => x.TaxpayerReference);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.StatusProjection).HasMaxLength(32);
        });

        modelBuilder.Entity<BillEvent>(entity =>
        {
            entity.HasIndex(x => x.Sequence).IsUnique();
            entity.HasIndex(x => new { x.AuthorityId, x.Sequence });
            entity.HasIndex(x => new { x.BillId, x.EventTime });
            entity.Property(x => x.EventType).HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<BillVerificationSnapshot>(entity =>
        {
            entity.HasIndex(x => x.VerificationRequestId);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Descriptor).HasMaxLength(140);
            entity.Property(x => x.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<BillPayment>(entity =>
        {
            entity.HasIndex(x => x.SipsTransactionId).IsUnique();
            entity.HasIndex(x => x.TxId).IsUnique();
            entity.HasIndex(x => new { x.BillId, x.Status });
            entity.HasIndex(x => new { x.AuthorityId, x.PaidAt });
            entity.HasIndex(x => new { x.AuthorityId, x.Status, x.PaidAt });
            entity.Property(x => x.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<BillPaymentAttempt>(entity =>
        {
            entity.HasIndex(x => x.TxId);
            entity.HasIndex(x => new { x.AuthorityId, x.CreatedAt });
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.HasIndex(x => new { x.Reason, x.CreatedAt });
            entity.Property(x => x.RawPayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<BillReversal>(entity =>
        {
            entity.HasIndex(x => new { x.OriginalSipsTransactionId, x.ReversalTransactionId }).IsUnique();
            entity.HasIndex(x => new { x.AuthorityId, x.ReversedAt });
            entity.Property(x => x.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<CallbackSubscription>().Property(x => x.SubscribedEventsJson).HasColumnType("jsonb");
        modelBuilder.Entity<CallbackDelivery>(entity =>
        {
            entity.HasIndex(x => new { x.AuthorityId, x.Status, x.CreatedAt });
            entity.HasIndex(x => new { x.CallbackSubscriptionId, x.Status });
            entity.HasIndex(x => x.EventId);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
        });
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
            entity.HasIndex(x => new { x.Type, x.Status, x.NextAttemptAt });
            entity.HasIndex(x => x.AggregateId);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
        });
        modelBuilder.Entity<IdempotencyRecord>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<HmacKeyVersion>().HasIndex(x => x.Version).IsUnique();
    }
}
