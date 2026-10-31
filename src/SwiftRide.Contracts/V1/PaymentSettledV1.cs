namespace SwiftRide.Contracts.V1;

public sealed record PaymentSettledV1(
    Guid EventId,
    Guid PaymentId,
    Guid TripId,
    string RiderId,
    string DriverId,
    decimal Amount,
    string Currency,
    string PaymentKey,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
