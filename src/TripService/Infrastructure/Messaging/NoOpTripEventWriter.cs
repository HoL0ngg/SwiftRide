using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Messaging;

namespace SwiftRide.TripService.Infrastructure.Messaging;

public sealed class NoOpTripEventWriter : ITripEventWriter
{
    public bool IsAvailable => false;
    public void Add(TripAcceptedV1 message) { }
    public void Add(RideCompletedV1 message) { }
    public void Add(PaymentCompensationRequestedV1 message) { }
}
