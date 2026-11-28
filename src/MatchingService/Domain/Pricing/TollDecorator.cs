namespace SwiftRide.MatchingService.Domain;

public sealed class TollDecorator(IFareComponent inner) : IFareComponent
{
    public decimal Calculate(PricingContext context) => inner.Calculate(context) + context.Toll;
}
