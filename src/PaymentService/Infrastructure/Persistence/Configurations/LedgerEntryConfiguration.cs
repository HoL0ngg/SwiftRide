using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Infrastructure.Persistence.Converters;
namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

// paydb.ledger_entries: PK (payment_id, side)
public sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> b)
    {
        b.ToTable("ledger_entries");
        b.HasKey(e => new { e.PaymentId, e.Direction });
        b.Property(e => e.PaymentId).HasColumnName("payment_id").ValueGeneratedNever();
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
