using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Repositories;
namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentRepository(PaymentDbContext db) : IPaymentReader, IPaymentWriter  // Adapter hiện thực 2 Port
{
    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct) =>
        WithChildren().FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) =>
        WithChildren().FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct);

    public Task<Payment?> GetByTripIdAsync(Guid tripId, CancellationToken ct) =>
        WithChildren().FirstOrDefaultAsync(p => p.TripId == tripId, ct);

    public void Add(Payment payment) => db.Payments.Add(payment);   // kéo theo LedgerEntries qua graph

    // Aggregate luôn nạp đủ: sổ cái + refund + sổ cái refund
    // AsSplitQuery: tránh nổ số dòng khi JOIN nhiều collection
    private IQueryable<Payment> WithChildren() => db.Payments
        .Include(p => p.LedgerEntries)
        .Include(p => p.Refund).ThenInclude(r => r!.LedgerEntries)
        .AsSplitQuery();
}
