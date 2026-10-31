using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Domain;

public sealed record TripSnapshot
{
    public TripSnapshot(
        Guid id,
        string riderId,
        TripStatus status,
        FareSnapshot? fare,
        bool isFareLocked,
        string? candidateDriverId,
        string? offeredDriverId,
        string? assignedDriverId,
        int rejectionCount,
        int maxDriverRejections,
        IReadOnlyList<string>? rejectedDriverIds,
        string? paymentIdempotencyKey,
        Guid? paymentId,
        PaymentFailureKind? lastPaymentFailure)
    {
        Id = id;
        RiderId = riderId;
        Status = status;
        Fare = fare;
        IsFareLocked = isFareLocked;
        CandidateDriverId = candidateDriverId;
        OfferedDriverId = offeredDriverId;
        AssignedDriverId = assignedDriverId;
        RejectionCount = rejectionCount;
        MaxDriverRejections = maxDriverRejections;
        RejectedDriverIds = rejectedDriverIds?.ToArray() ?? Array.Empty<string>();
        PaymentIdempotencyKey = paymentIdempotencyKey;
        PaymentId = paymentId;
        LastPaymentFailure = lastPaymentFailure;
    }

    public Guid Id { get; init; }
    public string RiderId { get; init; }
    public TripStatus Status { get; init; }
    public FareSnapshot? Fare { get; init; }
    public bool IsFareLocked { get; init; }
    public string? CandidateDriverId { get; init; }
    public string? OfferedDriverId { get; init; }
    public string? AssignedDriverId { get; init; }
    public int RejectionCount { get; init; }
    public int MaxDriverRejections { get; init; }
    public IReadOnlyList<string> RejectedDriverIds { get; init; }
    public string? PaymentIdempotencyKey { get; init; }
    public Guid? PaymentId { get; init; }
    public PaymentFailureKind? LastPaymentFailure { get; init; }
}
