namespace SwiftRide.Contracts.V1;

public sealed record PaymentFailedV1(
    Guid EventId,
    Guid? PaymentId,
    Guid TripId,
    string PaymentKey,
    string FailureCode,
    bool IsPermanent,
    string SafeErrorMessage,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
