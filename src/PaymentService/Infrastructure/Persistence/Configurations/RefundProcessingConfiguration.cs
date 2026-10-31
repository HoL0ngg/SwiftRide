using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

public sealed class RefundProcessingConfiguration : IEntityTypeConfiguration<RefundProcessingRecord>
{
    public void Configure(EntityTypeBuilder<RefundProcessingRecord> b)
    {
        b.ToTable("refund_processing", DbNames.Schema);
        b.HasKey(r => r.RefundId);
        b.Property(r => r.RefundId).HasColumnName("refund_id").ValueGeneratedNever();
        b.Property(r => r.PaymentId).HasColumnName("payment_id");
        b.Property(r => r.TripId).HasColumnName("trip_id");
        b.Property(r => r.PaymentKey).HasColumnName("payment_key").IsRequired();
        b.Property(r => r.Reason).HasColumnName("reason");
        b.Property(r => r.AttemptsStarted).HasColumnName("attempts_started");
        b.Property(r => r.OwnerId).HasColumnName("owner_id");
        b.Property(r => r.LeaseUntil).HasColumnName("lease_until");
        b.Property(r => r.Outcome).HasColumnName("outcome");
        b.Property(r => r.CreatedAt).HasColumnName("created_at");
        b.Property(r => r.UpdatedAt).HasColumnName("updated_at")
            .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
    }
}
