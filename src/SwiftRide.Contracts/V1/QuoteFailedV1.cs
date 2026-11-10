namespace SwiftRide.Contracts.V1;

/// <summary>
/// Matching -> Trip: no quote could be produced.
/// <see cref="ReasonCode"/> is "NoDriverAvailable" or "InvalidRequest".
/// </summary>
public sealed record QuoteFailedV1(
    Guid EventId,
    Guid TripId,
    string ReasonCode,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
