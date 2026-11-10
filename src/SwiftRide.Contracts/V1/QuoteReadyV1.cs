namespace SwiftRide.Contracts.V1;

/// <summary>Matching -> Trip: a quote and a candidate driver are available for the trip.</summary>
public sealed record QuoteReadyV1(
    Guid EventId,
    Guid TripId,
    Guid QuoteId,
    decimal Amount,
    string Currency,
    decimal DistanceKm,
    decimal EstimatedMinutes,
    string CandidateDriverId,
    double CandidateDistanceKm,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
