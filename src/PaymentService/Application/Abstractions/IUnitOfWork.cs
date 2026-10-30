namespace SwiftRide.PaymentService.Application.Abstractions;

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct);
}
