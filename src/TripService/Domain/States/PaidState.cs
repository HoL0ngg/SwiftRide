namespace SwiftRide.TripService.Domain.States;

internal sealed class PaidState : TripState
{
    public override TripStatus Status => TripStatus.Paid;
    public override bool IsTerminal => true;
}
