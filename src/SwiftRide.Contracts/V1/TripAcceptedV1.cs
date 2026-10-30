namespace SwiftRide.Contracts.V1;

public sealed record TripAcceptedV1(
    Guid EventId,
    Guid TripId,
    string RiderId,
    string DriverId,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
