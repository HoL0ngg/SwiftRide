namespace SwiftRide.MatchingService.Domain;

public sealed class SurgeDecorator(IFareComponent inner) : IFareComponent
{
    public decimal Calculate(PricingContext context) => inner.Calculate(context) * context.Surge;
}
