namespace SwiftRide.MatchingService.Domain;

public interface IFareComponent
{
    decimal Calculate(PricingContext context);
}

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

public sealed class SurgeDecorator(IFareComponent inner) : IFareComponent
{
    public decimal Calculate(PricingContext context) => inner.Calculate(context) * context.Surge;
}

public sealed class TollDecorator(IFareComponent inner) : IFareComponent
{
    public decimal Calculate(PricingContext context) => inner.Calculate(context) + context.Toll;
}

public sealed class PromotionDecorator(IFareComponent inner) : IFareComponent
{
    public decimal Calculate(PricingContext context) => inner.Calculate(context) * context.PromotionFactor;
}

public sealed class TaxDecorator(IFareComponent inner) : IFareComponent
{
    public decimal Calculate(PricingContext context) => inner.Calculate(context) * (1 + context.Tax);
}
