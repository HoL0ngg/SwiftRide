using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Application;

public sealed class MatchingOptions
{
    public double RadiusKm { get; set; } = 5;
    public int LocationMaxAgeSeconds { get; set; } = 300;
    public decimal AverageSpeedKph { get; set; } = 25;
    public decimal CostPerKm { get; set; } = 7500;
    public decimal CostPerMinute { get; set; } = 300;
    public decimal Surge { get; set; } = 1;
    public decimal Toll { get; set; }
    public decimal PromotionFactor { get; set; } = 1;
    public decimal Tax { get; set; }
    public MidpointRounding Rounding { get; set; } = MidpointRounding.AwayFromZero;

    public void Validate()
    {
        if (!double.IsFinite(RadiusKm) || RadiusKm <= 0 || LocationMaxAgeSeconds <= 0 || AverageSpeedKph <= 0 || !Enum.IsDefined(Rounding))
            throw new MatchingDomainException("Matching configuration is invalid.");
        new PricingContext(0, 0, CostPerKm, CostPerMinute, Surge, Toll, PromotionFactor, Tax).Validate();
    }
}
