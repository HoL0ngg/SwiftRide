using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

public sealed class PaymentInboxConfiguration : IEntityTypeConfiguration<PaymentInboxRecord>
{
    public void Configure(EntityTypeBuilder<PaymentInboxRecord> builder)
    {
        builder.ToTable("integration_inbox", DbNames.Schema);
        builder.HasKey(i => new { i.ConsumerName, i.MessageId });

        builder.Property(i => i.ConsumerName).HasColumnName("consumer_name").HasMaxLength(150);
        builder.Property(i => i.MessageId).HasColumnName("message_id");
        builder.Property(i => i.ReceivedAtUtc).HasColumnName("received_at_utc").HasDefaultValueSql("now()");
        builder.Property(i => i.ProcessedAtUtc).HasColumnName("processed_at_utc");
        builder.Property(i => i.LastError).HasColumnName("last_error");
    }
}
