using SwiftRide.PaymentService.Domain.Entities;
namespace SwiftRide.PaymentService.Domain.Repositories;

public interface IPaymentReader   // 7.1 ISP: use case chỉ đọc thì chỉ cần interface này
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct);
}