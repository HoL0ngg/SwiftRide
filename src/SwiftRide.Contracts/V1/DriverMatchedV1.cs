namespace SwiftRide.Contracts.V1;

/// <summary>
/// Matching -> Trip: a driver was found. <see cref="ExcludedDriverIds"/> echoes the exclusion list of the
/// request so Trip can extend it if the driver turns out to be busy.
/// </summary>
public sealed record DriverMatchedV1(
    Guid EventId,
    Guid TripId,
    string DriverId,
    double DistanceKm,
    IReadOnlyList<string> ExcludedDriverIds,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
