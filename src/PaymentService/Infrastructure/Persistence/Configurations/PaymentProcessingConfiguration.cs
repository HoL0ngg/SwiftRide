using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftRide.PaymentService.Domain.Entities;

namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

public sealed class PaymentProcessingConfiguration : IEntityTypeConfiguration<PaymentProcessingRecord>
{
    public void Configure(EntityTypeBuilder<PaymentProcessingRecord> builder)
    {
        builder.ToTable("payment_processing", DbNames.Schema);
        builder.HasKey(p => p.PaymentId);

        builder.Property(p => p.PaymentId).HasColumnName("payment_id");
        builder.HasOne<Payment>().WithOne().HasForeignKey<PaymentProcessingRecord>(p => p.PaymentId);

        builder.Property(p => p.RequestHash).HasColumnName("request_hash").IsRequired();
        builder.Property(p => p.Version).HasColumnName("version").HasDefaultValue(2);
        builder.Property(p => p.DriverId).HasColumnName("driver_id").IsRequired();
        builder.Property(p => p.AttemptsStarted).HasColumnName("attempts_started").HasDefaultValue(0);
        builder.Property(p => p.OwnerId).HasColumnName("owner_id");
        builder.Property(p => p.LeaseUntil).HasColumnName("lease_until");
        builder.Property(p => p.Outcome).HasColumnName("outcome").HasDefaultValue("pending").IsRequired();
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
    }
}
