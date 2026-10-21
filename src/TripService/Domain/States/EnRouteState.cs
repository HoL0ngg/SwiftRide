namespace SwiftRide.TripService.Domain.States;

internal sealed class EnRouteState : TripState
{
    public override TripStatus Status => TripStatus.EnRoute;

    public override void Pickup(Trip trip, string driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId) || driverId != trip.AssignedDriverId)
            throw new TripDomainException("InvalidDriver", "Driver ID does not match assigned driver.");

        trip.TransitionTo(new PickedUpState());
    }
}
