namespace SwiftRide.MatchingService.Domain;

public sealed record PricingContext(decimal DistanceKm, decimal EstimatedMinutes, decimal CostPerKm,
    decimal CostPerMinute, decimal Surge, decimal Toll, decimal PromotionFactor, decimal Tax)
{
    public void Validate()
    {
        if (DistanceKm < 0 || EstimatedMinutes < 0 || CostPerKm < 0 || CostPerMinute < 0 || Surge < 1 ||
            Toll < 0 || PromotionFactor <= 0 || PromotionFactor > 1 || Tax < 0)
            throw new MatchingDomainException("Pricing values are outside their valid range.");
    }
}
