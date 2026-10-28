using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Domain.Entities;
namespace SwiftRide.PaymentService.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore.ChangeTracking;
using SwiftRide.PaymentService.Domain.Enums;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundLedgerEntry> RefundLedgerEntries => Set<RefundLedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DbNames.Schema);                                    // mọi bảng nằm trong schema paydb
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentDbContext).Assembly);  // nạp mọi *Configuration
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // Rào chắn append-only: dữ liệu tiền chỉ được INSERT, cấm sửa/xóa
        ChangeTracker.DetectChanges();
        var tampered = ChangeTracker.Entries().Any(e =>
            e.Entity is Payment or LedgerEntry or Refund or RefundLedgerEntry
            && e.State is EntityState.Modified or EntityState.Deleted
            && !IsPendingPaymentFinalization(e));
        if (tampered)
            throw new InvalidOperationException("Payments, refunds and ledger entries are append-only.");
        return base.SaveChangesAsync(ct);
    }

    private static bool IsPendingPaymentFinalization(EntityEntry e) =>
    e.Entity is Payment && e.State == EntityState.Modified
    && e.Property(nameof(Payment.Status)).OriginalValue is PaymentStatus.Pending;
}
