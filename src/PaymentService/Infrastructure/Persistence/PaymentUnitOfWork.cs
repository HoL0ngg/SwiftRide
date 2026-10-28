using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentUnitOfWork(PaymentDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }                         // Tất cả INSERT trong 1 transaction
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } name
        } && DbNames.DuplicateRequestConstraints.Contains(name))
        {
            db.ChangeTracker.Clear();
            throw new DuplicateIdempotencyKeyException();              // Dịch lỗi hạ tầng -> lỗi ứng dụng
        }
    }
}
