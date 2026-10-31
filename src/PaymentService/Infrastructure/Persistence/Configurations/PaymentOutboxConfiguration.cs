using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

public sealed class PaymentOutboxConfiguration : IEntityTypeConfiguration<PaymentOutboxRecord>
{
    public void Configure(EntityTypeBuilder<PaymentOutboxRecord> builder)
    {
        builder.ToTable("integration_outbox", DbNames.Schema);
        builder.HasKey(o => o.MessageId);

        builder.Property(o => o.MessageId).HasColumnName("message_id");
        builder.Property(o => o.EventType).HasColumnName("event_type").HasMaxLength(150).IsRequired();
        builder.Property(o => o.PayloadJson).HasColumnName("payload_json").HasColumnType("text").IsRequired();
        builder.Property(o => o.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
        builder.Property(o => o.NextAttemptUtc).HasColumnName("next_attempt_utc");
        builder.Property(o => o.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        builder.Property(o => o.MaxAttempts).HasColumnName("max_attempts").HasDefaultValue(10);
        builder.Property(o => o.Owner).HasColumnName("owner").IsConcurrencyToken();
        builder.Property(o => o.LeaseUntilUtc).HasColumnName("lease_until_utc");
        builder.Property(o => o.PublishedAtUtc).HasColumnName("published_at_utc");
        builder.Property(o => o.LastError).HasColumnName("last_error");
        builder.Property(o => o.RequiresAction).HasColumnName("requires_action").HasDefaultValue(false);
        builder.Property(o => o.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100).IsRequired();
        builder.Property(o => o.CausationId).HasColumnName("causation_id");
        builder.Property(o => o.TraceParent).HasColumnName("trace_parent").HasMaxLength(256);

        builder.HasIndex(o => new { o.PublishedAtUtc, o.RequiresAction, o.NextAttemptUtc })
            .HasDatabaseName("idx_paydb_outbox_poll");
    }
}
