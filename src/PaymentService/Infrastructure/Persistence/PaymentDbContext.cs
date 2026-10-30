using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;

namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundLedgerEntry> RefundLedgerEntries => Set<RefundLedgerEntry>();
    public DbSet<PaymentProcessingRecord> PaymentProcessings => Set<PaymentProcessingRecord>();
    public DbSet<RefundProcessingRecord> RefundProcessings => Set<RefundProcessingRecord>();
    public DbSet<MockGatewayOperationRecord> MockGatewayOperations => Set<MockGatewayOperationRecord>();
    public DbSet<PaymentOutboxRecord> IntegrationOutbox => Set<PaymentOutboxRecord>();
    public DbSet<PaymentInboxRecord> IntegrationInbox => Set<PaymentInboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DbNames.Schema);                                    // mọi bảng nằm trong schema paydb
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentDbContext).Assembly);  // nạp mọi *Configuration
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // Rào chắn append-only: dữ liệu tiền chỉ được INSERT, cấm sửa/xóa ngoại trừ các mutation hợp lệ
        ChangeTracker.DetectChanges();
        var tampered = ChangeTracker.Entries().Any(e =>
            e.Entity is Payment or LedgerEntry or Refund or RefundLedgerEntry
            && e.State is EntityState.Modified or EntityState.Deleted
            && !IsPermittedEntityMutation(e));
        if (tampered)
            throw new InvalidOperationException("Payments, refunds and ledger entries are append-only.");
        return base.SaveChangesAsync(ct);
    }

    private static bool IsPermittedEntityMutation(EntityEntry e)
    {
        if (e.Entity is Payment && e.State == EntityState.Modified)
        {
            return e.Property(nameof(Payment.Status)).OriginalValue is PaymentStatus.Pending;
        }

        if (e.Entity is Refund && e.State == EntityState.Modified)
        {
            var origStatus = (RefundStatus)e.Property(nameof(Refund.Status)).OriginalValue!;
            return origStatus is RefundStatus.Pending or RefundStatus.Failed;
        }

        return false;
    }
}
