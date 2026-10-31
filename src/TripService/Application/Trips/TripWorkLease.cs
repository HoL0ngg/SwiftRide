namespace SwiftRide.TripService.Application.Trips;

public enum TripWorkKind
{
    Matching,
    Payment
}

public sealed record TripWorkLease(
    TripWorkKind Kind,
    Guid JobId,
    Guid TripId,
    Guid Owner,
    DateTimeOffset LeaseUntilUtc,
    int AttemptCount,
    int MaxAttempts = 20,
    DateTimeOffset? DeadlineUtc = null,
    string Transport = "Rest");
