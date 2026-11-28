namespace SwiftRide.MatchingService.Domain;

public sealed class FareCalculator
{
    private readonly IFareComponent _fare;

    public FareCalculator(IPricingStrategy strategy)
    {
        // Wrapping order preserves the pricing formula; round only after the full chain.
        IFareComponent fare = new BaseFare(strategy);
        fare = new SurgeDecorator(fare);
        fare = new TollDecorator(fare);
        fare = new PromotionDecorator(fare);
        _fare = new TaxDecorator(fare);
    }

    public decimal Calculate(PricingContext context, MidpointRounding rounding)
    {
        context.Validate();
        if (!Enum.IsDefined(rounding)) throw new MatchingDomainException("Invalid rounding policy.");
        try
        {
            var amount = _fare.Calculate(context);
            amount = decimal.Round(amount, 0, rounding);
            if (amount < 0 || amount >= 1_000_000_000_000_000_000m)
                throw new MatchingDomainException("Fare exceeds the supported range.");
            return amount;
        }
        catch (OverflowException) { throw new MatchingDomainException("Fare exceeds the supported range."); }
    }
}
