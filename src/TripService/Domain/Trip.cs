using SwiftRide.TripService.Domain.States;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Domain;

public class Trip
{
    private TripState _state;

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
}
