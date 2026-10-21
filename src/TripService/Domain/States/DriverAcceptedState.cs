namespace SwiftRide.TripService.Domain.States;

internal sealed class DriverAcceptedState : TripState
{
    public override TripStatus Status => TripStatus.DriverAccepted;

    public override void StartEnRoute(Trip trip, string driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId) || driverId != trip.AssignedDriverId)
            throw new TripDomainException("InvalidDriver", "Driver ID does not match assigned driver.");

        trip.TransitionTo(new EnRouteState());
    }
}
