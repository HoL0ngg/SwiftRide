using Moq;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Tests;

public class VehicleTypeTests
{
    [Theory]
    [InlineData("motorbike", "standard", 7500, 300, 9140)]
    [InlineData("motorbike", "night", 9000, 360, 10968)]
    [InlineData("car", "standard", 15000, 600, 18280)]
    [InlineData("car", "night", 18000, 720, 21937)]
    public async Task Vehicle_rates_are_selected_before_night_adjustment_and_saved_in_snapshot(
        string vehicleType, string mode, decimal expectedKmRate, decimal expectedMinuteRate, decimal expectedAmount)
    {
        var writer = new Mock<IQuoteWriter>();
        QuoteSnapshot? saved = null;
        writer.Setup(w => w.AddAsync(It.IsAny<QuoteSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<QuoteSnapshot, CancellationToken>((quote, _) => saved = quote)
            .Returns(Task.CompletedTask);
        var useCase = new CreateQuote(writer.Object,
            [new StandardPricingStrategy(), new NightPricingStrategy(1.2m)], new(), TimeProvider.System);

        var result = await useCase.ExecuteAsync(new(0, 0), new(0, .01), mode, default, vehicleType: vehicleType);

        Assert.NotNull(saved);
        Assert.Equal(vehicleType, saved.VehicleType);
        Assert.Equal(vehicleType, result.VehicleType);
        Assert.Equal(expectedKmRate, saved.Pricing.CostPerKm);
        Assert.Equal(expectedMinuteRate, saved.Pricing.CostPerMinute);
        Assert.Equal(expectedAmount, result.Amount);
        Assert.Equal(result.Amount, new FareCalculator(new StandardPricingStrategy()).Calculate(saved.Pricing, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task Car_matching_skips_nearer_motorbike_and_excluded_car_without_fallback()
    {
        var now = DateTimeOffset.UtcNow;
        var reader = new Mock<IDriverLocationReader>();
        reader.Setup(r => r.ReadAvailableAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new("bike", new(0, 0), true, now), new("car", new(0, .01), true, now, "car")]);
        var useCase = new MatchDriver(reader.Object, new(), TimeProvider.System);

        var matched = await useCase.ExecuteAsync(new(0, 0), [], default, "car");
        var none = await useCase.ExecuteAsync(new(0, 0), ["car"], default, "car");

        Assert.Equal("car", matched.Candidate!.DriverId);
        Assert.Equal("NoDriverAvailable", none.Status);
        Assert.Null(none.Candidate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("truck")]
    [InlineData(null)]
    public async Task Invalid_vehicle_type_is_rejected_before_persistence_or_matching(string? vehicleType)
    {
        var writer = new Mock<IQuoteWriter>(MockBehavior.Strict);
        var reader = new Mock<IDriverLocationReader>(MockBehavior.Strict);
        var quote = new CreateQuote(writer.Object, [new StandardPricingStrategy()], new(), TimeProvider.System);

        await Assert.ThrowsAsync<MatchingDomainException>(() => quote.ExecuteAsync(new(0, 0), new(0, 1), "standard", default, vehicleType: vehicleType!));
        await Assert.ThrowsAsync<MatchingDomainException>(() => new MatchDriver(reader.Object, new(), TimeProvider.System)
            .ExecuteAsync(new(0, 0), [], default, vehicleType!));
        Assert.Throws<MatchingDomainException>(() => new UpdateDriverLocation(new Mock<IDriverLocationWriter>(MockBehavior.Strict).Object, TimeProvider.System)
            .ExecuteAsync("driver", new(0, 0), true, default, vehicleType!));
    }

    [Fact]
    public async Task Location_update_normalizes_vehicle_type()
    {
        DriverLocation? saved = null;
        var writer = new Mock<IDriverLocationWriter>();
        writer.Setup(w => w.UpsertAsync(It.IsAny<DriverLocation>(), It.IsAny<CancellationToken>()))
            .Callback<DriverLocation, CancellationToken>((location, _) => saved = location)
            .Returns(Task.CompletedTask);

        await new UpdateDriverLocation(writer.Object, TimeProvider.System).ExecuteAsync("driver", new(0, 0), true, default, " CAR ");

        Assert.Equal("car", saved!.VehicleType);
    }

    [Fact]
    public void Invalid_car_rates_fail_configuration_validation() =>
        Assert.Throws<MatchingDomainException>(() => new MatchingOptions { CarCostPerKm = -1 }.Validate());
}
