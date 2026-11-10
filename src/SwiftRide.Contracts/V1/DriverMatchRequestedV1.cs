namespace SwiftRide.Contracts.V1;

/// <summary>Trip -> Matching: find the next driver for a confirmed trip, skipping the excluded drivers.</summary>
public sealed record DriverMatchRequestedV1(
    Guid EventId,
    Guid TripId,
    double PickupLatitude,
    double PickupLongitude,
    IReadOnlyList<string> ExcludedDriverIds,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
