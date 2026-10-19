namespace SwiftRide.TripService.Domain.States;

internal sealed class DroppedOffState : TripState
{
    public override TripStatus Status => TripStatus.DroppedOff;

    public override void RequestPayment(Trip trip)
    {
        var key = $"trip-{trip.Id:D}-payment";
        trip.SetPaymentIdempotencyKey(key);
        trip.TransitionTo(new PaymentPendingState());
    }
}
