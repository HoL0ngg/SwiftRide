using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Tests;

public class PricingTests
{
    [Fact]
    public void Fare_applies_steps_in_order_and_rounds_once()
    {
        var calculator = new FareCalculator(new StandardPricingStrategy());
        var context = new PricingContext(5, 10, 10000, 1000, 1.2m, 5000, .9m, .08m);
        Assert.Equal(74844m, calculator.Calculate(context, MidpointRounding.AwayFromZero));
    }

    [Theory]
    [InlineData(-1, 1, 0, 1, 0)]
    [InlineData(1, .9, 0, 1, 0)]
    [InlineData(1, 1, -1, 1, 0)]
    [InlineData(1, 1, 0, 0, 0)]
    [InlineData(1, 1, 0, 1.1, 0)]
    [InlineData(1, 1, 0, 1, -.1)]
    public void Rejects_invalid_pricing(decimal distance, decimal surge, decimal toll, decimal promotion, decimal tax)
    {
        Assert.Throws<MatchingDomainException>(() => new FareCalculator(new StandardPricingStrategy())
            .Calculate(new PricingContext(distance, 1, 1, 1, surge, toll, promotion, tax), MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Allows_zero_and_respects_midpoint_policy()
    {
        var calculator = new FareCalculator(new StandardPricingStrategy());
        Assert.Equal(0, calculator.Calculate(new(0, 0, 0, 0, 1, 0, 1, 0), MidpointRounding.AwayFromZero));
        var half = new PricingContext(1, 0, 2.5m, 0, 1, 0, 1, 0);
        Assert.Equal(3, calculator.Calculate(half, MidpointRounding.AwayFromZero));
        Assert.Equal(2, calculator.Calculate(half, MidpointRounding.ToEven));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(91, 0)]
    [InlineData(0, -181)]
    public void Rejects_invalid_coordinates(double latitude, double longitude) =>
        Assert.Throws<MatchingDomainException>(() => new GeoPoint(latitude, longitude));

    [Fact]
    public void Haversine_handles_same_point_antipodes_and_known_distance()
    {
        var origin = new GeoPoint(0, 0);
        Assert.Equal(0, origin.DistanceKmTo(origin));
        Assert.InRange(origin.DistanceKmTo(new(0, 1)), 111.19, 111.20);
        Assert.InRange(origin.DistanceKmTo(new(0, 180)), 20015, 20016);
    }
}
