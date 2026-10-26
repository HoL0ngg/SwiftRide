namespace SwiftRide.PaymentService.Application.Abstractions;

public interface IUnitOfWork { Task SaveChangesAsync(CancellationToken ct); }
// 10.1 1 lần lưu = 1 transaction local