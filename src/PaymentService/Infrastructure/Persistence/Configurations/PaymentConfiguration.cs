using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Infrastructure.Persistence.Converters;
namespace SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;

// Map entity Payment -> bảng paydb.payments (đã tồn tại trên Supabase, KHÔNG dùng EF migration)
public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();   // Id sinh ở Domain; BẮT BUỘC để EF coi dòng mới là Added
        b.Property(p => p.TripId).HasColumnName("trip_id");
        b.Property(p => p.RiderId).HasColumnName("rider_id").IsRequired();
        b.Property(p => p.Method).HasColumnName("method").HasConversion<LowerCaseEnumConverter<PaymentMethod>>();
        b.Property(p => p.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        b.Property(p => p.RequestFingerprint).HasColumnName("request_fingerprint").IsRequired();
        b.Property(p => p.Status).HasColumnName("status").HasConversion<LowerCaseEnumConverter<PaymentStatus>>();
        b.Property(p => p.GatewayReference).HasColumnName("gateway_reference");
        b.Property(p => p.FailureCode).HasColumnName("failure_code");
        b.Property(p => p.CreatedAt).HasColumnName("created_at");
        b.Property(p => p.UpdatedAt).HasColumnName("updated_at")
    .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
        b.Property(p => p.SettledAt).HasColumnName("settled_at");

        b.OwnsOne(p => p.Amount, m =>                                      // Money -> 2 cột amount, currency
        {
            m.Property(x => x.Amount).HasColumnName("amount");
            m.Property(x => x.Currency).HasColumnName("currency");
        });
        b.Navigation(p => p.Amount).IsRequired();

        b.Ignore(p => p.DriverId);   // bảng payments không có driver_id: suy ra từ sổ cái

        b.HasMany(p => p.LedgerEntries).WithOne().HasForeignKey(e => e.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Navigation(p => p.LedgerEntries).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasOne(p => p.Refund).WithOne().HasForeignKey<Refund>(r => r.PaymentId)   // 0..1 refund / payment
            .OnDelete(DeleteBehavior.Restrict);
        b.Navigation(p => p.Refund).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
