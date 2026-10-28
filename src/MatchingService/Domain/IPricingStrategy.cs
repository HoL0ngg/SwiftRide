namespace SwiftRide.MatchingService.Domain;

public interface IPricingStrategy
{
    string Mode { get; }
    decimal CalculateBase(PricingContext context);
}

public sealed class StandardPricingStrategy : IPricingStrategy
{
    public string Mode => "standard";
    public decimal CalculateBase(PricingContext context)
    {
        context.Validate();
        return context.CostPerKm * context.DistanceKm + context.CostPerMinute * context.EstimatedMinutes;
    }
}
