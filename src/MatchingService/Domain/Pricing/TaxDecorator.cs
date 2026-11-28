namespace SwiftRide.MatchingService.Domain;

public sealed class TaxDecorator(IFareComponent inner) : IFareComponent
{
    public decimal Calculate(PricingContext context) => inner.Calculate(context) * (1 + context.Tax);
}
