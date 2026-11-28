namespace SwiftRide.MatchingService.Domain;

public sealed class BaseFare(IPricingStrategy strategy) : IFareComponent
{
    public decimal Calculate(PricingContext context)
    {
        var amount = strategy.CalculateBase(context);
        if (amount < 0)
            throw new MatchingDomainException("A pricing strategy cannot return a negative fare.");
        return amount;
    }
}
