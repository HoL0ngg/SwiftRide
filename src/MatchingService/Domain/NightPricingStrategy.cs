namespace SwiftRide.MatchingService.Domain;

public sealed class NightPricingStrategy : IPricingStrategy
{
    private readonly decimal _multiplier;
    private readonly StandardPricingStrategy _standard = new();

    public NightPricingStrategy(decimal multiplier)
    {
        if (multiplier < 1)
            throw new MatchingDomainException("Night multiplier must be at least 1.");
        _multiplier = multiplier;
    }

    public string Mode => "night";

    public PricingContext PrepareContext(PricingContext context)
    {
        context.Validate();
        try
        {
            // Snapshot the effective rates so the stored quote can reproduce the fare.
            return context with
            {
                CostPerKm = context.CostPerKm * _multiplier,
                CostPerMinute = context.CostPerMinute * _multiplier
            };
        }
        catch (OverflowException)
        {
            throw new MatchingDomainException("Night pricing rates exceed the supported range.");
        }
    }

    public decimal CalculateBase(PricingContext context) => _standard.CalculateBase(context);
}
