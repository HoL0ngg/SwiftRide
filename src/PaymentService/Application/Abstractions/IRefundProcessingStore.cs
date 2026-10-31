namespace SwiftRide.PaymentService.Application.Abstractions;

public sealed record RefundProcessingClaimResult(
    ClaimStatus Status,
    int AttemptsStarted,
    DateTimeOffset? LeaseUntil,
    bool CanRetry,
    bool RequiresAction);

public interface IRefundProcessingStore
{
    Task InsertAsync(Guid refundId, Guid paymentId, Guid tripId, string paymentKey, string? reason, DateTimeOffset now, CancellationToken ct);
    Task<RefundProcessingClaimResult> TryClaimAsync(Guid refundId, Guid owner, DateTimeOffset now, TimeSpan leaseDuration, int maxAttempts, CancellationToken ct);
    Task<bool> CompleteAsync(Guid refundId, Guid owner, string outcome, CancellationToken ct);
}
