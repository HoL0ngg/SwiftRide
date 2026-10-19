namespace SwiftRide.TripService.Domain.States;

public sealed class PaidState : TripState
{
    public override TripStatus Status => TripStatus.Paid;
    public override bool IsTerminal => true;
}
