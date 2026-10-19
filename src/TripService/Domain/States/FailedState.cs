namespace SwiftRide.TripService.Domain.States;

internal sealed class FailedState : TripState
{
    public override TripStatus Status => TripStatus.Failed;
    public override bool IsTerminal => true;
}
