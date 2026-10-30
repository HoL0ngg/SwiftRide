namespace SwiftRide.TripService.Application.Abstractions;

public interface IMatchingClient
{
    Task<MatchOutcome> MatchAsync(MatchInput input, CancellationToken ct);
    Task<QuotedFare> QuoteAsync(QuoteInput input, CancellationToken ct);
}
