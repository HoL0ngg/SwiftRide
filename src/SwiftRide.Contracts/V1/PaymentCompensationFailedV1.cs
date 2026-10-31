namespace SwiftRide.Contracts.V1;

public sealed record PaymentCompensationFailedV1(
    Guid EventId,
    Guid? RefundId,
    Guid PaymentId,
    Guid TripId,
    string ReasonCode,
    string SafeErrorMessage,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
