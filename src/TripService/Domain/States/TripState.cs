using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Domain.States;

internal abstract class TripState
{
    public abstract TripStatus Status { get; }
    public virtual bool IsTerminal => false;

    public virtual void ApplyQuote(Trip trip, FareSnapshot fare, string candidateDriverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot apply quote in state {Status}.");

    public virtual void FailNoDriverAvailable(Trip trip) =>
        throw new TripDomainException("InvalidTransition", $"Cannot fail for no driver in state {Status}.");

    public virtual void ConfirmQuote(Trip trip, string riderId, Guid quoteId, string offeredDriverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot confirm quote in state {Status}.");

    public virtual void ConfirmQuote(Trip trip, string riderId, Guid quoteId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot confirm quote in state {Status}.");

    public virtual void RejectQuote(Trip trip, string riderId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot reject quote in state {Status}.");

    public virtual void Cancel(Trip trip, string riderId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot cancel trip in state {Status}.");

    public virtual void AcceptDriver(Trip trip, string driverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot accept driver in state {Status}.");

    public virtual void RejectDriver(Trip trip, string driverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot reject driver in state {Status}.");

    public virtual void OfferReplacementDriver(Trip trip, string driverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot offer replacement driver in state {Status}.");

    public virtual void StartEnRoute(Trip trip, string driverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot start en-route in state {Status}.");

    public virtual void Pickup(Trip trip, string driverId) =>
        throw new TripDomainException("InvalidTransition", $"Cannot pick up rider in state {Status}.");

    public virtual void Dropoff(Trip trip, string driverId, DateTimeOffset occurredAtUtc) =>
        throw new TripDomainException("InvalidTransition", $"Cannot drop off rider in state {Status}.");

    public virtual void RequestPayment(Trip trip) =>
        throw new TripDomainException("InvalidTransition", $"Cannot request payment in state {Status}.");

    public virtual void SettlePayment(Trip trip, PaymentSettlement settlement) =>
        throw new TripDomainException("InvalidTransition", $"Cannot settle payment in state {Status}.");

    public virtual void RecordPaymentFailure(Trip trip, PaymentFailureKind failureKind) =>
        throw new TripDomainException("InvalidTransition", $"Cannot record payment failure in state {Status}.");

    public virtual void WithdrawOffer(Trip trip) =>
        throw new TripDomainException("InvalidTransition", $"Cannot withdraw offer in state {Status}.");
}
