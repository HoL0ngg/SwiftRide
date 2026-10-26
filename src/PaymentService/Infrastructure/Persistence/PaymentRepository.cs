using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Repositories;
namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentRepository(PaymentDbContext db) : IPaymentReader, IPaymentWriter  // 22.1 Adapter hiện thực 2 Port
{
    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Payments.Include(p => p.LedgerEntries).FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) =>
        db.Payments.Include(p => p.LedgerEntries).FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct);

    public void Add(Payment payment) => db.Payments.Add(payment);   // 22.2 Kéo theo LedgerEntries qua graph
}