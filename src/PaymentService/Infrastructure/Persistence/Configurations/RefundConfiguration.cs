using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Infrastructure.Persistence.Converters;
namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

// paydb.refunds (UNIQUE payment_id) + paydb.refund_ledger_entries (PK refund_id, side)
public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.ToTable("refunds");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(r => r.PaymentId).HasColumnName("payment_id");
        b.Property(r => r.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        b.Property(r => r.RequestFingerprint).HasColumnName("request_fingerprint").IsRequired();
        b.Property(r => r.Reason).HasColumnName("reason").IsRequired();
        b.Property(r => r.Status).HasColumnName("status").HasConversion<LowerCaseEnumConverter<RefundStatus>>();
        b.Property(r => r.GatewayReference).HasColumnName("gateway_reference");
        b.Property(r => r.FailureCode).HasColumnName("failure_code");
        b.Property(r => r.CreatedAt).HasColumnName("created_at");
        b.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        b.Property(r => r.SettledAt).HasColumnName("settled_at");
        b.OwnsOne(r => r.Amount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("amount");
            m.Property(x => x.Currency).HasColumnName("currency");
        });
        b.Navigation(r => r.Amount).IsRequired();

        b.HasMany(r => r.LedgerEntries).WithOne().HasForeignKey(e => e.RefundId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Navigation(r => r.LedgerEntries).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class RefundLedgerEntryConfiguration : IEntityTypeConfiguration<RefundLedgerEntry>
{
    public void Configure(EntityTypeBuilder<RefundLedgerEntry> b)
    {
        b.ToTable("refund_ledger_entries");
        b.HasKey(e => new { e.RefundId, e.Direction });
        b.Property(e => e.RefundId).HasColumnName("refund_id").ValueGeneratedNever();
        b.Property(e => e.Direction).HasColumnName("side").HasConversion<LowerCaseEnumConverter<EntryDirection>>().ValueGeneratedNever();
        b.Property(e => e.Account).HasColumnName("account_code").IsRequired();
        b.Property(e => e.CreatedAt).HasColumnName("created_at");
        b.OwnsOne(e => e.Amount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("amount");
            m.Property(x => x.Currency).HasColumnName("currency");
        });
        b.Navigation(e => e.Amount).IsRequired();
    }
}
