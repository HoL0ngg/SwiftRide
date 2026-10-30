using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Repositories;

namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentRepository(PaymentDbContext db) : IPaymentReader, IPaymentWriter  // Adapter hiện thực 2 Port
{
    public async Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await WithChildren().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await WithChildren().FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct);
    }

    public async Task<Payment?> GetByTripIdAsync(Guid tripId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await WithChildren().FirstOrDefaultAsync(p => p.TripId == tripId, ct);
    }

    public void Add(Payment payment) => db.Payments.Add(payment);   // kéo theo LedgerEntries qua graph

    // Aggregate luôn nạp đủ: sổ cái + refund + sổ cái refund trong một single atomic JOIN query
    private IQueryable<Payment> WithChildren() => db.Payments
        .Include(p => p.LedgerEntries)
        .Include(p => p.Refund).ThenInclude(r => r!.LedgerEntries);
}
