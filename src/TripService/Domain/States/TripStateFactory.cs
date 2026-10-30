namespace SwiftRide.TripService.Domain.States;

internal static class TripStateFactory
{
    public static TripState Create(TripStatus status) => status switch
    {
        TripStatus.Requested => new RequestedState(),
        TripStatus.Quoted => new QuotedState(),
        TripStatus.DriverOffered => new DriverOfferedState(),
        TripStatus.DriverAccepted => new DriverAcceptedState(),
        TripStatus.EnRoute => new EnRouteState(),
        TripStatus.PickedUp => new PickedUpState(),
        TripStatus.DroppedOff => new DroppedOffState(),
        TripStatus.PaymentPending => new PaymentPendingState(),
        TripStatus.Paid => new PaidState(),
        TripStatus.Cancelled => new CancelledState(),
        TripStatus.Failed => new FailedState(),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown trip status.")
    };
}
