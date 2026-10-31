using SwiftRide.TripService.Domain;

namespace SwiftRide.TripService.Application.Trips;

public sealed class TripContext
{
    public TripContext(
        Trip trip,
        string pickupName,
        string pickupAddress,
        double pickupLat,
        double pickupLon,
        string destName,
        string destAddress,
        double destLat,
        double destLon,
        string paymentMethod,
        string mockTokenAlias,
        decimal? distanceKm,
        decimal? estimatedMinutes,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        byte[]? rowVersion = null,
        string paymentTransport = "Rest",
        string pricingMode = "standard")
    {
        Trip = trip ?? throw new ArgumentNullException(nameof(trip));
        PickupName = pickupName;
        PickupAddress = pickupAddress;
        PickupLat = pickupLat;
        PickupLon = pickupLon;
        DestName = destName;
        DestAddress = destAddress;
        DestLat = destLat;
        DestLon = destLon;
        PaymentMethod = paymentMethod;
        MockTokenAlias = mockTokenAlias;
        DistanceKm = distanceKm;
        EstimatedMinutes = estimatedMinutes;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        RowVersion = rowVersion;
        PaymentTransport = paymentTransport;
        PricingMode = pricingMode;
    }

    public Trip Trip { get; }
    public string PickupName { get; }
    public string PickupAddress { get; }
    public double PickupLat { get; }
    public double PickupLon { get; }
    public string DestName { get; }
    public string DestAddress { get; }
    public double DestLat { get; }
    public double DestLon { get; }
    public string PaymentMethod { get; }
    public string MockTokenAlias { get; }
    public decimal? DistanceKm { get; set; }
    public decimal? EstimatedMinutes { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[]? RowVersion { get; set; }
    public string PaymentTransport { get; set; }
    public string PricingMode { get; set; }
}
