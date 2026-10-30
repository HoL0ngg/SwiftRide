namespace SwiftRide.TripService.Domain.States;

internal sealed class QuotedState : TripState
{
    public override TripStatus Status => TripStatus.Quoted;

    public override void ConfirmQuote(Trip trip, string riderId, Guid quoteId, string offeredDriverId)
    {
        if (riderId != trip.RiderId)
            throw new TripDomainException("InvalidRider", "Only the trip rider can confirm quote.");

        if (trip.Fare == null || quoteId != trip.Fare.QuoteId)
            throw new TripDomainException("InvalidQuote", "Quote ID does not match trip quote.");

        if (string.IsNullOrWhiteSpace(offeredDriverId))
            throw new TripDomainException("InvalidDriver", "Offered driver ID cannot be null or empty.");

        trip.LockFare();
        trip.SetOfferedDriver(offeredDriverId);
        trip.TransitionTo(new DriverOfferedState());
    }

    public override void ConfirmQuote(Trip trip, string riderId, Guid quoteId)
    {
        if (riderId != trip.RiderId)
            throw new TripDomainException("InvalidRider", "Only the trip rider can confirm quote.");

        if (trip.Fare == null || quoteId != trip.Fare.QuoteId)
            throw new TripDomainException("InvalidQuote", "Quote ID does not match trip quote.");

        trip.LockFare();
        trip.SetOfferedDriver(null);
        trip.TransitionTo(new DriverOfferedState());
    }

    public override void RejectQuote(Trip trip, string riderId)
    {
        if (riderId != trip.RiderId)
            throw new TripDomainException("InvalidRider", "Only the trip rider can reject quote.");

        trip.TransitionTo(new CancelledState());
    }

    public override void Cancel(Trip trip, string riderId)
    {
        if (riderId != trip.RiderId)
            throw new TripDomainException("InvalidRider", "Only the trip rider can cancel.");

        trip.TransitionTo(new CancelledState());
    }
}
