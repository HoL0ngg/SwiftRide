using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class TripRecordConfiguration : IEntityTypeConfiguration<TripRecord>
{
    public void Configure(EntityTypeBuilder<TripRecord> builder)
    {
        builder.ToTable("Trips");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.RiderId).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Status).HasMaxLength(50).IsRequired();
        builder.Property(t => t.FareCurrency).HasMaxLength(10);
        builder.Property(t => t.FareAmount).HasColumnType("decimal(18,0)");
        builder.Property(t => t.DistanceKm).HasColumnType("decimal(18,2)");
        builder.Property(t => t.EstimatedMinutes).HasColumnType("decimal(18,2)");

        builder.Property(t => t.PickupName).HasMaxLength(200);
        builder.Property(t => t.PickupAddress).HasMaxLength(500);
        builder.Property(t => t.DestName).HasMaxLength(200);
        builder.Property(t => t.DestAddress).HasMaxLength(500);
        builder.Property(t => t.PaymentMethod).HasMaxLength(50);
        builder.Property(t => t.MockTokenAlias).HasMaxLength(100);
        builder.Property(t => t.CandidateDriverId).HasMaxLength(100);
        builder.Property(t => t.OfferedDriverId).HasMaxLength(100);
        builder.Property(t => t.AssignedDriverId).HasMaxLength(100);
        builder.Property(t => t.PaymentIdempotencyKey).HasMaxLength(100);
        builder.Property(t => t.LastPaymentFailure).HasMaxLength(50);
        builder.Property(t => t.PaymentTransport).HasMaxLength(20).HasDefaultValue("Rest");
        builder.Property(t => t.PricingMode).HasMaxLength(50).HasDefaultValue("standard");

        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.HasMany(t => t.Rejections)
            .WithOne(r => r.Trip)
            .HasForeignKey(r => r.TripId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TripDriverRejectionConfiguration : IEntityTypeConfiguration<TripDriverRejectionRecord>
{
    public void Configure(EntityTypeBuilder<TripDriverRejectionRecord> builder)
    {
        builder.ToTable("TripDriverRejections");
        builder.HasKey(r => new { r.TripId, r.DriverId });
        builder.Property(r => r.DriverId).HasMaxLength(100).IsRequired();
    }
}

public sealed class ActiveRiderTripConfiguration : IEntityTypeConfiguration<ActiveRiderTripRecord>
{
    public void Configure(EntityTypeBuilder<ActiveRiderTripRecord> builder)
    {
        builder.ToTable("ActiveRiderTrips");
        builder.HasKey(a => a.RiderId);
        builder.Property(a => a.RiderId).HasMaxLength(100);
        builder.HasIndex(a => a.TripId).IsUnique();
    }
}

public sealed class ActiveDriverAssignmentConfiguration : IEntityTypeConfiguration<ActiveDriverAssignmentRecord>
{
    public void Configure(EntityTypeBuilder<ActiveDriverAssignmentRecord> builder)
    {
        builder.ToTable("ActiveDriverAssignments");
        builder.HasKey(a => a.DriverId);
        builder.Property(a => a.DriverId).HasMaxLength(100);
        builder.HasIndex(a => a.TripId).IsUnique();
    }
}

public sealed class CommandReceiptConfiguration : IEntityTypeConfiguration<CommandReceiptRecord>
{
    public void Configure(EntityTypeBuilder<CommandReceiptRecord> builder)
    {
        builder.ToTable("CommandReceipts");
        builder.HasKey(c => new { c.Scope, c.Key });
        builder.Property(c => c.Scope).HasMaxLength(200);
        builder.Property(c => c.Key).HasMaxLength(100);
        builder.Property(c => c.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Status).HasMaxLength(50).IsRequired();
    }
}

public sealed class TripPaymentJobConfiguration : IEntityTypeConfiguration<TripPaymentJobRecord>
{
    public void Configure(EntityTypeBuilder<TripPaymentJobRecord> builder)
    {
        builder.ToTable("TripPaymentJobs");
        builder.HasKey(j => j.TripId);
        builder.Property(j => j.PaymentKey).HasMaxLength(100).IsRequired();
        builder.Property(j => j.Status).HasMaxLength(50).IsRequired();
        builder.Property(j => j.Transport).HasMaxLength(20).HasDefaultValue("Rest");
        builder.Property(j => j.Owner).IsConcurrencyToken();
    }
}

public sealed class IntegrationOutboxConfiguration : IEntityTypeConfiguration<IntegrationOutboxRecord>
{
    public void Configure(EntityTypeBuilder<IntegrationOutboxRecord> builder)
    {
        builder.ToTable("IntegrationOutbox");
        builder.HasKey(o => o.MessageId);
        builder.Property(o => o.EventType).HasMaxLength(150).IsRequired();
        builder.Property(o => o.CorrelationId).HasMaxLength(100).IsRequired();
        builder.Property(o => o.TraceParent).HasMaxLength(256);
        builder.Property(o => o.Owner).IsConcurrencyToken();
        builder.Property(o => o.LastError).HasMaxLength(4000);
        builder.HasIndex(o => new { o.PublishedAtUtc, o.RequiresAction, o.NextAttemptUtc });
    }
}

public sealed class IntegrationInboxConfiguration : IEntityTypeConfiguration<IntegrationInboxRecord>
{
    public void Configure(EntityTypeBuilder<IntegrationInboxRecord> builder)
    {
        builder.ToTable("IntegrationInbox");
        builder.HasKey(i => new { i.ConsumerName, i.MessageId });
        builder.Property(i => i.ConsumerName).HasMaxLength(150);
        builder.Property(i => i.LastError).HasMaxLength(4000);
    }
}
