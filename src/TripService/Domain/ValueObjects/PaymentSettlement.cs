namespace SwiftRide.TripService.Domain.ValueObjects;

public sealed class PaymentSettlement
{
    public PaymentSettlement(Guid paymentId, Guid tripId, decimal amount, string currency, string idempotencyKey)
    {
        if (paymentId == Guid.Empty)
            throw new TripDomainException("InvalidPaymentResult", "Payment ID cannot be empty.");

        if (tripId == Guid.Empty)
            throw new TripDomainException("InvalidPaymentResult", "Trip ID cannot be empty.");

        if (amount < 0m || amount % 1m != 0m)
            throw new TripDomainException("InvalidPaymentResult", "Amount must be a non-negative whole integer.");

        if (string.IsNullOrWhiteSpace(currency))
            throw new TripDomainException("InvalidPaymentResult", "Currency cannot be null or empty.");

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new TripDomainException("InvalidPaymentResult", "Idempotency key cannot be null or empty.");

        PaymentId = paymentId;
        TripId = tripId;
        Amount = amount;
        Currency = currency;
        IdempotencyKey = idempotencyKey;
    }

    public Guid PaymentId { get; }
    public Guid TripId { get; }
    public decimal Amount { get; }
    public string Currency { get; }
    public string IdempotencyKey { get; }
}
