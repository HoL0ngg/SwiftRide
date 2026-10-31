namespace SwiftRide.Contracts.V1;

public sealed record PaymentRefundedV1(
    Guid EventId,
    Guid RefundId,
    Guid PaymentId,
    Guid TripId,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId = null);
