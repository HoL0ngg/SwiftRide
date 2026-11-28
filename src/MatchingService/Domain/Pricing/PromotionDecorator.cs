namespace SwiftRide.MatchingService.Domain;

public sealed class PromotionDecorator(IFareComponent inner) : IFareComponent
{
    public decimal Calculate(PricingContext context) => inner.Calculate(context) * context.PromotionFactor;
}
