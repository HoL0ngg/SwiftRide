using Moq;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Tests;

public class MatchingUseCaseTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-28T00:00:00Z");
    }

    [Fact]
    public async Task Matching_excludes_invalid_candidates_and_breaks_ties_by_id()
    {
        var clock = new Clock();
        var now = clock.GetUtcNow();
        var origin = new GeoPoint(0, 0);
        DriverLocation[] locations = [new("offline", origin, false, now), new("stale", origin, true, now.AddHours(-1)),
            new("excluded", origin, true, now), new("far", new(10, 10), true, now),
            new("future", origin, true, now.AddMinutes(1)), new("b", new(0, .01), true, now), new("a", new(0, .01), true, now)];
        var reader = new Mock<IDriverLocationReader>();
        reader.Setup(r => r.ReadAvailableAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(locations);
        var useCase = new MatchDriver(reader.Object, new(), clock);
        var result = await useCase.ExecuteAsync(origin, ["excluded"], default);
        Assert.Equal("Matched", result.Status);
        Assert.Equal("a", result.Candidate!.DriverId);
        Assert.InRange(result.Candidate.DistanceKm, 1.11, 1.12);
        var none = await useCase.ExecuteAsync(origin, ["a", "b", "excluded"], default);
        Assert.Equal("NoDriverAvailable", none.Status);
        Assert.Null(none.Candidate);
    }

    [Fact]
    public async Task Quote_persists_server_calculated_snapshot_and_propagates_failure()
    {
        var writer = new Mock<IQuoteWriter>();
        QuoteSnapshot? saved = null;
        writer.Setup(w => w.AddAsync(It.IsAny<QuoteSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<QuoteSnapshot, CancellationToken>((q, _) => saved = q).Returns(Task.CompletedTask);
        var useCase = new CreateQuote(writer.Object, [new StandardPricingStrategy()], new(), new Clock());
        var result = await useCase.ExecuteAsync(new(0, 0), new(0, 1), "standard", default);
        Assert.NotNull(saved);
        Assert.Equal(saved.Id, result.QuoteId);
        Assert.Equal(saved.Amount, result.Amount);
        Assert.Equal(saved.Pricing.EstimatedMinutes, result.DistanceKm / 25 * 60);
        Assert.Equal("VND", result.Currency);
        Assert.Equal(decimal.Truncate(result.Amount), result.Amount);
        writer.Setup(w => w.AddAsync(It.IsAny<QuoteSnapshot>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(new(0, 0), new(0, 1), "standard", default));
    }

    [Fact]
    public async Task Invalid_mode_does_not_write_and_location_uses_server_timestamp()
    {
        var writer = new Mock<IQuoteWriter>(MockBehavior.Strict);
        var useCase = new CreateQuote(writer.Object, [new StandardPricingStrategy()], new(), new Clock());
        await Assert.ThrowsAsync<MatchingDomainException>(() => useCase.ExecuteAsync(new(0, 0), new(0, 1), "unknown", default));
        var locations = new Mock<IDriverLocationWriter>();
        var clock = new Clock();
        using var cts = new CancellationTokenSource();
        await new UpdateDriverLocation(locations.Object, clock).ExecuteAsync("driver-1", new(1, 2), true, cts.Token);
        locations.Verify(w => w.UpsertAsync(It.Is<DriverLocation>(l => l.DriverId == "driver-1" && l.UpdatedAt == clock.GetUtcNow()), cts.Token), Times.Once);
    }

    private sealed class PremiumTestPricingStrategy : IPricingStrategy
    {
        public string Mode => "premium";
        public decimal CalculateBase(PricingContext context)
        {
            context.Validate();
            return (context.CostPerKm * context.DistanceKm + context.CostPerMinute * context.EstimatedMinutes) * 1.5m;
        }
    }

    [Fact]
    public async Task Quote_WithCustomPricingStrategy_CalculatesCorrectlyWithoutModifyingStandardStrategy()
    {
        var writer = new Mock<IQuoteWriter>();
        QuoteSnapshot? saved = null;
        writer.Setup(w => w.AddAsync(It.IsAny<QuoteSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<QuoteSnapshot, CancellationToken>((q, _) => saved = q).Returns(Task.CompletedTask);

        IPricingStrategy[] strategies = [new StandardPricingStrategy(), new PremiumTestPricingStrategy()];
        var useCase = new CreateQuote(writer.Object, strategies, new(), new Clock());

        var standardResult = await useCase.ExecuteAsync(new(0, 0), new(0, 1), "standard", default);
        var premiumResult = await useCase.ExecuteAsync(new(0, 0), new(0, 1), "premium", default);

        Assert.NotNull(saved);
        Assert.True(premiumResult.Amount > standardResult.Amount);
        Assert.Equal("VND", premiumResult.Currency);
    }
}