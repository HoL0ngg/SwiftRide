namespace SwiftRide.Contracts.V1;

/// <summary>Trip -> Matching: find the nearest driver and create a price quote for a new trip.</summary>
public sealed record QuoteRequestedV1(
    Guid EventId,
    Guid TripId,
    double PickupLatitude,
    double PickupLongitude,
    double DestinationLatitude,
    double DestinationLongitude,
    string Mode,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
