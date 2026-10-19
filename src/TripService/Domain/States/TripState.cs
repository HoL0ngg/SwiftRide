using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Domain.States;

public abstract class TripState
{
    public abstract TripStatus Status { get; }
    public virtual bool IsTerminal => false;

    public virtual void ApplyQuote(Trip trip, FareSnapshot fare, string candidateDriverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot apply quote in state {Status}.");

    public virtual void FailNoDriverAvailable(Trip trip) =>
        throw new TripDomainException("InvalidTransition", $"Cannot fail for no driver in state {Status}.");

    public virtual void ConfirmQuote(Trip trip, string riderId, Guid quoteId, string offeredDriverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot confirm quote in state {Status}.");

    public virtual void RejectQuote(Trip trip, string riderId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot reject quote in state {Status}.");

    public virtual void Cancel(Trip trip, string riderId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot cancel trip in state {Status}.");

    public virtual void RejectDriver(Trip trip, string driverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot reject driver in state {Status}.");

    public virtual void OfferReplacementDriver(Trip trip, string driverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot offer replacement driver in state {Status}.");
}
