namespace SwiftRide.Contracts.V1;

/// <summary>Matching -> Trip: no driver could be found. ReasonCode is "NoDriverAvailable" or "InvalidRequest".</summary>
public sealed record DriverMatchFailedV1(
    Guid EventId,
    Guid TripId,
    string ReasonCode,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
