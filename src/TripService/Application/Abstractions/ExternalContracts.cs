namespace SwiftRide.TripService.Application.Abstractions;

public sealed record GeoCoordinate(double Latitude, double Longitude);

public sealed record MatchInput(GeoCoordinate Pickup, IReadOnlyList<string> ExcludedDriverIds);

public enum MatchStatus
{
    Matched,
    NoDriverAvailable
}

public sealed record CandidateDriver(string DriverId, double DistanceKm);

public sealed record MatchOutcome(MatchStatus Status, CandidateDriver? Candidate);

public sealed record QuoteInput(GeoCoordinate Pickup, GeoCoordinate Destination, string Mode = "standard");

public sealed record QuotedFare(
    Guid QuoteId,
    decimal Amount,
    string Currency,
    decimal DistanceKm,
    decimal EstimatedMinutes,
    DateTimeOffset CreatedAt);

public sealed record ChargeRequest(
    Guid TripId,
    string RiderId,
    string? DriverId,
    decimal Amount,
    string Currency,
    string Method,
    string PaymentToken,
    string IdempotencyKey);

public enum RemotePaymentStatus
{
    Pending,
    Settled,
    Failed
}

public sealed record PaymentProcessingObservation(
    int AttemptsStarted,
    DateTimeOffset? LeaseUntilUtc,
    bool CanRetry,
    bool RequiresAction);

public sealed record PaymentObservation(
    Guid Id,
    Guid TripId,
    string RiderId,
    string? DriverId,
    decimal Amount,
    string Currency,
    RemotePaymentStatus Status,
    string? FailureCode,
    bool HasSettledRefund,
    PaymentProcessingObservation? Processing,
    TimeSpan? RetryAfter);
