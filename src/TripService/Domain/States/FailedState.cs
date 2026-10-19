namespace SwiftRide.TripService.Domain.States;

public sealed class FailedState : TripState
{
    public override TripStatus Status => TripStatus.Failed;
    public override bool IsTerminal => true;
}
