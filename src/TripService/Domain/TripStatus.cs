namespace SwiftRide.TripService.Domain;

public enum TripStatus
{
    Requested,
    Quoted,
    DriverOffered,
    DriverAccepted,
    EnRoute,
    PickedUp,
    DroppedOff,
    PaymentPending,
    Paid,
    Cancelled,
    Failed
}
