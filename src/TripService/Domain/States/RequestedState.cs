using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Domain.States;

internal sealed class RequestedState : TripState
{
    public override TripStatus Status => TripStatus.Requested;

    public override void ApplyQuote(Trip trip, FareSnapshot fare, string candidateDriverId)
    {
        if (fare == null)
            throw new TripDomainException("InvalidFare", "Fare snapshot cannot be null.");

        if (string.IsNullOrWhiteSpace(candidateDriverId))
            throw new TripDomainException("InvalidDriver", "Candidate driver ID cannot be null or empty.");

        trip.SetFare(fare);
        trip.SetCandidateDriver(candidateDriverId);
        trip.TransitionTo(new QuotedState());
    }

    public override void FailNoDriverAvailable(Trip trip)
    {
        trip.TransitionTo(new FailedState());
    }

    public override void Cancel(Trip trip, string riderId)
    {
        if (riderId != trip.RiderId)
            throw new TripDomainException("InvalidRider", "Only the trip rider can cancel.");

        trip.TransitionTo(new CancelledState());
    }
}
