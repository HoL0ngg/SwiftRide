using SwiftRide.TripService.Domain.Events;
using SwiftRide.TripService.Domain.States;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Domain;

public class Trip
{
    private TripState _state;
    private readonly List<string> _rejectedDriverIds = new();
    private readonly List<IDomainEvent> _domainEvents = new();

    private Trip(Guid id, string riderId, int maxDriverRejections)
    {
        Id = id;
        RiderId = riderId;
        MaxDriverRejections = maxDriverRejections;
        _state = new RequestedState();
    }

    public Guid Id { get; }
    public string RiderId { get; }
    public TripStatus Status => _state.Status;
    public bool IsTerminal => _state.IsTerminal;
    public FareSnapshot? Fare { get; private set; }
    public bool IsFareLocked { get; private set; }
    public string? CandidateDriverId { get; private set; }
    public string? OfferedDriverId { get; private set; }
    public string? AssignedDriverId { get; private set; }
    public int RejectionCount { get; private set; }
    public int MaxDriverRejections { get; }
    public IReadOnlyCollection<string> RejectedDriverIds => _rejectedDriverIds.ToArray();
    public bool IsRematchPending => Status == TripStatus.DriverOffered && OfferedDriverId == null;
    public string? PaymentIdempotencyKey { get; private set; }
    public Guid? PaymentId { get; private set; }
    public PaymentFailureKind? LastPaymentFailure { get; private set; }
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    internal void TransitionTo(TripState newState) => _state = newState;
    internal void SetFare(FareSnapshot fare) => Fare = fare;
    internal void LockFare() => IsFareLocked = true;
    internal void SetCandidateDriver(string driverId) => CandidateDriverId = driverId;
    internal void SetOfferedDriver(string? driverId) => OfferedDriverId = driverId;
    internal void SetAssignedDriver(string? driverId) => AssignedDriverId = driverId;
    internal void AddRejectedDriver(string driverId) => _rejectedDriverIds.Add(driverId);
    internal void IncrementRejections() => RejectionCount++;
    internal void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    internal void SetPaymentIdempotencyKey(string key) => PaymentIdempotencyKey = key;
    internal void SetPaymentId(Guid paymentId) => PaymentId = paymentId;
    internal void SetLastPaymentFailure(PaymentFailureKind failureKind) => LastPaymentFailure = failureKind;
    internal void ClearLastPaymentFailure() => LastPaymentFailure = null;

    public static Trip Request(Guid id, string riderId, int maxDriverRejections = 3)
    {
        if (id == Guid.Empty)
            throw new TripDomainException("InvalidTripId", "Trip ID cannot be empty.");

        if (string.IsNullOrWhiteSpace(riderId))
            throw new TripDomainException("InvalidRider", "Rider ID cannot be null or empty.");

        if (maxDriverRejections < 0)
            throw new TripDomainException("InvalidRejectionLimit", "Max driver rejections must be non-negative.");

        return new Trip(id, riderId, maxDriverRejections);
    }

    public void ApplyQuote(FareSnapshot fare, string candidateDriverId) =>
        _state.ApplyQuote(this, fare, candidateDriverId);

    public void FailNoDriverAvailable() =>
        _state.FailNoDriverAvailable(this);

    public void ConfirmQuote(string riderId, Guid quoteId, string offeredDriverId) =>
        _state.ConfirmQuote(this, riderId, quoteId, offeredDriverId);

    public void RejectQuote(string riderId) =>
        _state.RejectQuote(this, riderId);

    public void Cancel(string riderId) =>
        _state.Cancel(this, riderId);

    public void RejectDriver(string driverId) =>
        _state.RejectDriver(this, driverId);

    public void OfferReplacementDriver(string driverId) =>
        _state.OfferReplacementDriver(this, driverId);

    public void AcceptDriver(string driverId) =>
        _state.AcceptDriver(this, driverId);

    public void StartEnRoute(string driverId) =>
        _state.StartEnRoute(this, driverId);

    public void Pickup(string driverId) =>
        _state.Pickup(this, driverId);

    public void Dropoff(string driverId, DateTimeOffset occurredAtUtc) =>
        _state.Dropoff(this, driverId, occurredAtUtc);

    public void RequestPayment() =>
        _state.RequestPayment(this);

    public void SettlePayment(PaymentSettlement settlement) =>
        _state.SettlePayment(this, settlement);

    public void RecordPaymentFailure(PaymentFailureKind failureKind) =>
        _state.RecordPaymentFailure(this, failureKind);
}
