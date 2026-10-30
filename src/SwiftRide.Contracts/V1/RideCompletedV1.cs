namespace SwiftRide.Contracts.V1;

public sealed record RideCompletedV1(
    Guid EventId,
    Guid TripId,
    string RiderId,
    string DriverId,
    Guid QuoteId,
    decimal FareAmount,
    string FareCurrency,
    string PaymentMethod,
    string MockTokenAlias,
    string PaymentIdempotencyKey,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
