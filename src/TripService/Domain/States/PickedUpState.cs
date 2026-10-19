using SwiftRide.TripService.Domain.Events;

namespace SwiftRide.TripService.Domain.States;

public sealed class PickedUpState : TripState
{
    public override TripStatus Status => TripStatus.PickedUp;

    public override void Dropoff(Trip trip, string driverId, DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(driverId) || driverId != trip.AssignedDriverId)
            throw new TripDomainException("InvalidDriver", "Driver ID does not match assigned driver.");

        var utcTime = occurredAtUtc.ToUniversalTime();
        var rideCompleted = new RideCompleted(
            trip.Id,
            trip.RiderId,
            trip.AssignedDriverId!,
            trip.Fare!.QuoteId,
            trip.Fare.Amount,
            trip.Fare.Currency,
            utcTime);

        trip.AddDomainEvent(rideCompleted);
        trip.TransitionTo(new DroppedOffState());
    }
}
