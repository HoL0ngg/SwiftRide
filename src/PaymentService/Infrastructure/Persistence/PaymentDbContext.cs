using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Domain.Entities;
namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    // 2. EF quét các DbSet: thấy Payment và LedgerEntry -> "vậy có 2 bảng"
    //    Rồi đọc các thuộc tính của 2 class này (Id, TripId, Status, ...) -> thành các cột

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentDbContext).Assembly); // 20.1 Nạp mọi *Configuration
    // 3. EF gọi hàm này để dựng mô hình. Dòng này đi tìm mọi class
    //    IEntityTypeConfiguration<...> trong project -> gọi Configure() của
    //    PaymentConfiguration và LedgerEntryConfiguration
    //    -> biết tên bảng, độ dài cột, unique index, khóa ngoại, xmin...

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // 20.2 Rào chắn append-only: cấm sửa/xóa dòng sổ cái
        if (ChangeTracker.Entries<LedgerEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Ledger entries are append-only.");
        return base.SaveChangesAsync(ct);
    }
}