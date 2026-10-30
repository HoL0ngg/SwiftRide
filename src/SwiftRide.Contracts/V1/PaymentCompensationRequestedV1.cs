namespace SwiftRide.Contracts.V1;

public sealed record PaymentCompensationRequestedV1(
    Guid EventId,
    Guid TripId,
    Guid PaymentId,
    string PaymentKey,
    decimal Amount,
    string Currency,
    string ReasonCode,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
