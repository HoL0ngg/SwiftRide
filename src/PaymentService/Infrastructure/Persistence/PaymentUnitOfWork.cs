using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Infrastructure.Persistence.Configurations;
namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentUnitOfWork(PaymentDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }                         // Tất cả INSERT/UPDATE trong 1 transaction
        catch (DbUpdateConcurrencyException)                           // xmin lệch: request khác đã sửa (vd hoàn tiền song song)
        { db.ChangeTracker.Clear(); throw new ConcurrentPaymentUpdateException(); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: PaymentConfiguration.IdempotencyKeyIndex })
        { db.ChangeTracker.Clear(); throw new DuplicateIdempotencyKeyException(); } // Dịch lỗi hạ tầng -> lỗi ứng dụng
    }
}