namespace SwiftRide.TripService.Domain.States;

public sealed class DroppedOffState : TripState
{
    public override TripStatus Status => TripStatus.DroppedOff;
}
