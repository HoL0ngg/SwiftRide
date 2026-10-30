using SwiftRide.Contracts.V1;

namespace SwiftRide.TripService.Application.Messaging;

public interface ITripEventWriter
{
    bool IsAvailable => true;
    void Add(TripAcceptedV1 message);
    void Add(RideCompletedV1 message);
    void Add(PaymentCompensationRequestedV1 message);
}
