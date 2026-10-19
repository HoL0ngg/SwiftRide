namespace SwiftRide.TripService.Domain.States;

public sealed class RequestedState : TripState
{
    public override TripStatus Status => TripStatus.Requested;
}
