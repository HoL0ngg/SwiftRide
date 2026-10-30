namespace SwiftRide.TripService.Application.Abstractions;

public interface IPaymentClient
{
    Task<PaymentObservation?> GetByTripAsync(Guid tripId, CancellationToken ct);
    Task<PaymentObservation> ChargeAsync(ChargeRequest input, CancellationToken ct);
}
