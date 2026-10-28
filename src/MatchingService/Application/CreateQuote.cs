using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Application;

public sealed class CreateQuote(IQuoteWriter writer, IEnumerable<IPricingStrategy> strategies, MatchingOptions options, TimeProvider clock)
{
    public async Task<QuoteResult> ExecuteAsync(GeoPoint pickup, GeoPoint destination, string mode, CancellationToken cancellationToken)
    {
        var strategy = strategies.FirstOrDefault(s => s.Mode == mode)
            ?? throw new MatchingDomainException("Unsupported pricing mode.");
        var distance = (decimal)pickup.DistanceKmTo(destination);
        decimal minutes;
        try { minutes = distance / options.AverageSpeedKph * 60; }
        catch (OverflowException) { throw new MatchingDomainException("Estimated duration exceeds the supported range."); }
        var context = new PricingContext(distance, minutes, options.CostPerKm, options.CostPerMinute,
            options.Surge, options.Toll, options.PromotionFactor, options.Tax);
        var amount = new FareCalculator(strategy).Calculate(context, options.Rounding);
        var quote = new QuoteSnapshot(Guid.NewGuid(), context, amount, "VND", clock.GetUtcNow());
        await writer.AddAsync(quote, cancellationToken);
        return new(quote.Id, amount, quote.Currency, distance, minutes, context, quote.CreatedAt);
    }
}
