using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftRide.PaymentService.Domain.Entities;
namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public const string IdempotencyKeyIndex = "ux_payments_idempotency_key";

    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();                       // 21.1 Id sinh ở Domain; BẮT BUỘC để EF coi dòng sổ cái mới là Added
        b.Property(p => p.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.HasIndex(p => p.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex); // 21.2 Unique index theo AGENTS
        b.HasIndex(p => p.TripId);
        b.Property(p => p.RequestFingerprint).HasMaxLength(64).IsRequired();
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(20);    // 21.3 Lưu enum dạng chữ cho dễ đọc DB
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.GatewayReference).HasMaxLength(100);
        b.Property(p => p.FailureReason).HasMaxLength(500);
        b.Property(p => p.RefundReason).HasMaxLength(500);

        b.OwnsOne(p => p.Amount, m =>                                      // 21.4 Money -> 2 cột Amount, Currency
        {
            m.Property(x => x.Amount).HasColumnName("Amount").HasPrecision(18, 0);
            m.Property(x => x.Currency).HasColumnName("Currency").HasMaxLength(3);
        });
        b.Navigation(p => p.Amount).IsRequired();

        b.HasMany(p => p.LedgerEntries).WithOne().HasForeignKey(e => e.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);                            // 21.5 Không cho xóa payment kéo theo sổ cái
        b.Navigation(p => p.LedgerEntries).UsePropertyAccessMode(PropertyAccessMode.Field); // 21.6 EF ghi thẳng vào _ledgerEntries

        b.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken(); // 21.7 Khóa lạc quan của PostgreSQL
    }
}