namespace SwiftRide.TripService.Domain.ValueObjects;

public sealed class FareSnapshot
{
    private const decimal MaxAmount = 1_000_000_000_000_000_000m; // 1e18

    public FareSnapshot(Guid quoteId, decimal amount, string currency)
    {
        if (quoteId == Guid.Empty)
            throw new TripDomainException("InvalidQuote", "Quote ID cannot be empty.");

        if (amount < 0m || amount >= MaxAmount || amount % 1m != 0m)
            throw new TripDomainException("InvalidFare", "Fare amount must be a non-negative whole integer less than 10^18.");

        if (string.IsNullOrWhiteSpace(currency) || currency != "VND")
            throw new TripDomainException("InvalidFare", "Currency must be VND.");

        QuoteId = quoteId;
        Amount = amount;
        Currency = currency;
    }

    public Guid QuoteId { get; }
    public decimal Amount { get; }
    public string Currency { get; }
}
