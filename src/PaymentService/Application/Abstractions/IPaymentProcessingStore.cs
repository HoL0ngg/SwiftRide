using SwiftRide.PaymentService.Application.Payments.Dtos;

namespace SwiftRide.PaymentService.Application.Abstractions;

public enum ClaimStatus
{
    Claimed,
    AlreadyCompleted,
    CurrentlyLeased,
    MaxAttemptsExceeded
}

public sealed record ProcessingClaimResult(
    ClaimStatus Status,
    int AttemptsStarted,
    DateTimeOffset? LeaseUntil,
    string? DriverId,
    bool CanRetry,
    bool RequiresAction);

public interface IPaymentProcessingStore
{
    Task<PaymentProcessingDto?> GetAsync(Guid paymentId, CancellationToken ct);
    Task<string?> GetDriverIdAsync(Guid paymentId, CancellationToken ct);
    Task InsertAsync(Guid paymentId, string requestHash, string driverId, DateTimeOffset now, CancellationToken ct);
    Task<ProcessingClaimResult> TryClaimAsync(Guid paymentId, Guid owner, DateTimeOffset now, TimeSpan leaseDuration, int maxAttempts, CancellationToken ct);
    Task<bool> CompleteAsync(Guid paymentId, Guid owner, string outcome, CancellationToken ct);
}
