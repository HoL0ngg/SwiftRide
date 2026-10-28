using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Application;

public sealed record DriverLocation(string DriverId, GeoPoint Position, bool IsOnline, DateTimeOffset UpdatedAt);
public sealed record QuoteSnapshot(Guid Id, PricingContext Pricing, decimal Amount, string Currency, DateTimeOffset CreatedAt);
public sealed record DriverCandidate(string DriverId, double DistanceKm);
public sealed record MatchResult(string Status, DriverCandidate? Candidate);
public sealed record QuoteResult(Guid QuoteId, decimal Amount, string Currency, decimal DistanceKm,
    decimal EstimatedMinutes, PricingContext Breakdown, DateTimeOffset CreatedAt);

public interface IDriverLocationReader
{
    Task<IReadOnlyList<DriverLocation>> ReadAvailableAsync(DateTimeOffset since, CancellationToken cancellationToken);
}
public interface IDriverLocationWriter
{
    Task UpsertAsync(DriverLocation location, CancellationToken cancellationToken);
}
public interface IQuoteWriter
{
    Task AddAsync(QuoteSnapshot quote, CancellationToken cancellationToken);
}
