namespace SwiftRide.TripService.Domain.States;

public abstract class TripState
{
    public abstract TripStatus Status { get; }
    public virtual bool IsTerminal => false;
}
