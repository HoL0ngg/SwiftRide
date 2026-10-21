namespace SwiftRide.TripService.Domain;

public enum PaymentFailureKind
{
    Transient,
    UnknownResult,
    Permanent,
    RetryLimitReached
}
