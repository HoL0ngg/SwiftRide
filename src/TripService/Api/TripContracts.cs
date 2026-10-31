namespace SwiftRide.TripService.Api;

public sealed record GeoPointRequest(double Latitude, double Longitude);

public sealed record CreateTripRequest(
    GeoPointRequest Pickup,
    string? PickupName,
    string? PickupAddress,
    GeoPointRequest Destination,
    string? DestinationName,
    string? DestinationAddress,
    string PaymentMethod,
    string? Mode = "standard",
    string? PaymentTransport = null);

public sealed record ConfirmQuoteRequest(Guid QuoteId);

public sealed record CancelTripRequest(string? Reason);

public sealed record GeoPointResponse(double Latitude, double Longitude);

public sealed record TripResponse(
    Guid Id,
    string RiderId,
    string Status,
    string? CandidateDriverId,
    string? OfferedDriverId,
    string? AssignedDriverId,
    int RejectionCount,
    bool RematchPending,
    string? QuoteId,
    string? FareAmount,
    string? Currency,
    decimal? DistanceKm,
    decimal? EstimatedMinutes,
    string PickupName,
    string PickupAddress,
    GeoPointResponse Pickup,
    string DestinationName,
    string DestinationAddress,
    GeoPointResponse Destination,
    string PaymentMethod,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DriverFeedApiResponse(TripResponse? OfferedTrip, TripResponse? ActiveTrip);

public sealed record TripHistoryPageApiResponse(IReadOnlyList<TripResponse> Items, string? NextCursor);
