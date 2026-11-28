namespace SwiftRide.MatchingService.Domain;

public interface IFareComponent
{
    decimal Calculate(PricingContext context);
}
