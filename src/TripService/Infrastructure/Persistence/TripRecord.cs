using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class TripRecord
{
    public Guid Id { get; set; }
    public string RiderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? QuoteId { get; set; }
    public decimal? FareAmount { get; set; }
    public string? FareCurrency { get; set; }
    public bool IsFareLocked { get; set; }
    public string? CandidateDriverId { get; set; }
    public string? OfferedDriverId { get; set; }
    public string? AssignedDriverId { get; set; }
    public int RejectionCount { get; set; }
    public int MaxDriverRejections { get; set; }
    public string? PaymentIdempotencyKey { get; set; }
    public Guid? PaymentId { get; set; }
    public string? LastPaymentFailure { get; set; }
    public string PaymentTransport { get; set; } = "Rest";
    public string PricingMode { get; set; } = "standard";

    public string PickupName { get; set; } = string.Empty;
    public string PickupAddress { get; set; } = string.Empty;
    public double PickupLat { get; set; }
    public double PickupLon { get; set; }
    public string DestName { get; set; } = string.Empty;
    public string DestAddress { get; set; } = string.Empty;
    public double DestLat { get; set; }
    public double DestLon { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public string MockTokenAlias { get; set; } = string.Empty;
    public decimal? DistanceKm { get; set; }
    public decimal? EstimatedMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[]? RowVersion { get; set; }
    public List<TripDriverRejectionRecord> Rejections { get; set; } = new();

    public TripContext ToContext()
    {
        FareSnapshot? fare = null;
        if (QuoteId.HasValue && FareAmount.HasValue)
        {
            fare = new FareSnapshot(QuoteId.Value, FareAmount.Value, FareCurrency ?? "VND");
        }

        PaymentFailureKind? lastFailure = null;
        if (!string.IsNullOrWhiteSpace(LastPaymentFailure) &&
            Enum.TryParse<PaymentFailureKind>(LastPaymentFailure, out var parsedFailure))
        {
            lastFailure = parsedFailure;
        }

        var status = Enum.Parse<TripStatus>(Status);
        var snapshot = new TripSnapshot(
            Id,
            RiderId,
            status,
            fare,
            IsFareLocked,
            CandidateDriverId,
            OfferedDriverId,
            AssignedDriverId,
            RejectionCount,
            MaxDriverRejections,
            Rejections.Select(r => r.DriverId).ToList(),
            PaymentIdempotencyKey,
            PaymentId,
            lastFailure);

        var restoredTrip = Trip.Restore(snapshot);

        return new TripContext(
            restoredTrip,
            PickupName,
            PickupAddress,
            PickupLat,
            PickupLon,
            DestName,
            DestAddress,
            DestLat,
            DestLon,
            PaymentMethod,
            MockTokenAlias,
            DistanceKm,
            EstimatedMinutes,
            CreatedAt,
            UpdatedAt,
            RowVersion,
            PaymentTransport,
            PricingMode);
    }
}

public sealed class TripDriverRejectionRecord
{
    public Guid TripId { get; set; }
    public string DriverId { get; set; } = string.Empty;
    public TripRecord Trip { get; set; } = null!;
}

public sealed class ActiveRiderTripRecord
{
    public string RiderId { get; set; } = string.Empty;
    public Guid TripId { get; set; }
}

public sealed class ActiveDriverAssignmentRecord
{
    public string DriverId { get; set; } = string.Empty;
    public Guid TripId { get; set; }
}

public sealed class CommandReceiptRecord
{
    public string Scope { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string? ResponseJson { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TripPaymentJobRecord
{
    public Guid TripId { get; set; }
    public string PaymentKey { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public Guid? Owner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public bool RequiresAction { get; set; }
    public string Transport { get; set; } = "Rest";
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
