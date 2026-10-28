namespace SwiftRide.MatchingService.Domain;

public interface IFareStep
{
    decimal Apply(decimal amount, PricingContext context);
}

public sealed class SurgeStep : IFareStep
{
    public decimal Apply(decimal amount, PricingContext context) => amount * context.Surge;
}
public sealed class TollStep : IFareStep
{
    public decimal Apply(decimal amount, PricingContext context) => amount + context.Toll;
}
public sealed class PromotionStep : IFareStep
{
    public decimal Apply(decimal amount, PricingContext context) => amount * context.PromotionFactor;
}
public sealed class TaxStep : IFareStep
{
    public decimal Apply(decimal amount, PricingContext context) => amount * (1 + context.Tax);
}

public sealed class FareCalculator(IPricingStrategy strategy)
{
    private static readonly IFareStep[] Steps = [new SurgeStep(), new TollStep(), new PromotionStep(), new TaxStep()];

    public decimal Calculate(PricingContext context, MidpointRounding rounding)
    {
        context.Validate();
        if (!Enum.IsDefined(rounding)) throw new MatchingDomainException("Invalid rounding policy.");
        try
        {
            var amount = strategy.CalculateBase(context);
            if (amount < 0) throw new MatchingDomainException("A pricing strategy cannot return a negative fare.");
            foreach (var step in Steps) amount = step.Apply(amount, context);
            amount = decimal.Round(amount, 0, rounding);
            if (amount < 0 || amount >= 1_000_000_000_000_000_000m)
                throw new MatchingDomainException("Fare exceeds the supported range.");
            return amount;
        }
        catch (OverflowException) { throw new MatchingDomainException("Fare exceeds the supported range."); }
    }
}
