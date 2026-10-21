using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftRide.PaymentService.Domain.Entities;
namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

public sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> b)
    {
        b.ToTable("ledger_entries");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Account).HasMaxLength(100).IsRequired();
        b.Property(e => e.Direction).HasConversion<string>().HasMaxLength(10);
        b.Property(e => e.Type).HasConversion<string>().HasMaxLength(10);
        b.OwnsOne(e => e.Amount, m =>
        {
            m.Property(x => x.Amount).HasColumnName("Amount").HasPrecision(18, 0);
            m.Property(x => x.Currency).HasColumnName("Currency").HasMaxLength(3);
        });
        b.Navigation(e => e.Amount).IsRequired();
        b.HasIndex(e => e.PaymentId);
    }
}