using SwiftRide.TripService.Application.Abstractions;

namespace SwiftRide.TripService.Application.Trips;

public sealed record CreateTripCommand(
    TripActor Actor,
    string CommandKey,
    GeoCoordinate Pickup,
    string? PickupName,
    string? PickupAddress,
    GeoCoordinate Destination,
    string? DestinationName,
    string? DestinationAddress,
    string PaymentMethod,
    string Mode = "standard",
    string? PaymentTransport = null);

public abstract record TripCommand(string ActionName);

public sealed record ConfirmQuoteCommand(Guid QuoteId) : TripCommand("confirm-quote");

public sealed record DriverAcceptCommand() : TripCommand("driver-accept");

public sealed record DriverRejectCommand() : TripCommand("driver-reject");

public sealed record EnRouteCommand() : TripCommand("en-route");

public sealed record PickupCommand() : TripCommand("pickup");

public sealed record DropoffCommand() : TripCommand("dropoff");

public sealed record CancelTripCommand(string? Reason = null) : TripCommand("cancel");

public sealed record TripCommandOutcome(
    TripReadModel Trip,
    bool IsReplay,
    string Action);

public sealed record DriverFeedResponse(TripReadModel? OfferedTrip, TripReadModel? ActiveTrip);

public sealed record TripHistoryPageResponse(IReadOnlyList<TripReadModel> Items, string? NextCursor);

public sealed record TripReadModel(
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
    double PickupLatitude,
    double PickupLongitude,
    string DestinationName,
    string DestinationAddress,
    double DestinationLatitude,
    double DestinationLongitude,
    string PaymentMethod,
    string MockTokenAlias,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateTripResult(
    TripReadModel Trip,
    bool IsReplay,
    bool IsPendingWork);

public static class TripMapper
{
    public static TripReadModel ToReadModel(TripContext ctx)
    {
        var trip = ctx.Trip;
        var fareStr = trip.Fare is not null ? trip.Fare.Amount.ToString("0") : null;
        var quoteIdStr = trip.Fare is not null ? trip.Fare.QuoteId.ToString("D") : null;
        var currency = trip.Fare?.Currency;

        return new TripReadModel(
            trip.Id,
            trip.RiderId,
            trip.Status.ToString(),
            trip.CandidateDriverId,
            trip.OfferedDriverId,
            trip.AssignedDriverId,
            trip.RejectionCount,
            trip.IsRematchPending,
            quoteIdStr,
            fareStr,
            currency,
            ctx.DistanceKm,
            ctx.EstimatedMinutes,
            ctx.PickupName,
            ctx.PickupAddress,
            ctx.PickupLat,
            ctx.PickupLon,
            ctx.DestName,
            ctx.DestAddress,
            ctx.DestLat,
            ctx.DestLon,
            ctx.PaymentMethod,
            ctx.MockTokenAlias,
            ctx.CreatedAt,
            ctx.UpdatedAt);
    }
}
