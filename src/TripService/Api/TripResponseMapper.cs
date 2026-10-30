using SwiftRide.TripService.Application.Trips;

namespace SwiftRide.TripService.Api;

public static class TripResponseMapper
{
    public static TripResponse ToResponse(TripReadModel model)
    {
        return new TripResponse(
            model.Id,
            model.RiderId,
            model.Status,
            model.CandidateDriverId,
            model.OfferedDriverId,
            model.AssignedDriverId,
            model.RejectionCount,
            model.RematchPending,
            model.QuoteId,
            model.FareAmount,
            model.Currency,
            model.DistanceKm,
            model.EstimatedMinutes,
            model.PickupName,
            model.PickupAddress,
            new GeoPointResponse(model.PickupLatitude, model.PickupLongitude),
            model.DestinationName,
            model.DestinationAddress,
            new GeoPointResponse(model.DestinationLatitude, model.DestinationLongitude),
            model.PaymentMethod,
            model.CreatedAt,
            model.UpdatedAt);
    }
}
