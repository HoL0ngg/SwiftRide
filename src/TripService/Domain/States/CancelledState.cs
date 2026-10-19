namespace SwiftRide.TripService.Domain.States;

public sealed class CancelledState : TripState
{
    public override TripStatus Status => TripStatus.Cancelled;
    public override bool IsTerminal => true;
}
