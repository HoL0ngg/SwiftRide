namespace SwiftRide.TripService.Domain.States;

internal sealed class DriverOfferedState : TripState
{
    public override TripStatus Status => TripStatus.DriverOffered;

    public override void AcceptDriver(Trip trip, string driverId)
    {
        if (trip.IsRematchPending)
            throw new TripDomainException("RematchPending", "Cannot accept while rematch is pending.");

        if (string.IsNullOrWhiteSpace(driverId) || driverId != trip.OfferedDriverId)
            throw new TripDomainException("InvalidDriver", "Driver ID does not match offered driver.");

        trip.SetAssignedDriver(driverId);
        trip.SetOfferedDriver(null);
        trip.TransitionTo(new DriverAcceptedState());
    }

    public override void RejectDriver(Trip trip, string driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId) || trip.OfferedDriverId == null || driverId != trip.OfferedDriverId)
            throw new TripDomainException("InvalidDriver", "Driver ID does not match currently offered driver.");

        trip.AddRejectedDriver(driverId);
        trip.SetOfferedDriver(null);
        trip.IncrementRejections();

        if (trip.RejectionCount > trip.MaxDriverRejections)
        {
            trip.TransitionTo(new FailedState());
        }
    }

    public override void OfferReplacementDriver(Trip trip, string driverId)
    {
        if (!trip.IsRematchPending)
            throw new TripDomainException("InvalidTransition", "Cannot offer replacement driver when an offer is active.");

        if (string.IsNullOrWhiteSpace(driverId))
            throw new TripDomainException("InvalidDriver", "Replacement driver ID cannot be null or empty.");

        if (trip.RejectedDriverIds.Contains(driverId))
            throw new TripDomainException("DriverAlreadyRejected", "Driver has already been rejected for this trip.");

        trip.SetOfferedDriver(driverId);
    }

    public override void FailNoDriverAvailable(Trip trip)
    {
        if (!trip.IsRematchPending)
            throw new TripDomainException("InvalidTransition", "Cannot fail for no driver while an offer is active.");

        trip.TransitionTo(new FailedState());
    }

    public override void Cancel(Trip trip, string riderId)
    {
        if (riderId != trip.RiderId)
            throw new TripDomainException("InvalidRider", "Only the trip rider can cancel.");

        trip.SetOfferedDriver(null);
        trip.TransitionTo(new CancelledState());
    }
}
