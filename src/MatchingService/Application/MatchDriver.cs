using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Application;

public sealed class MatchDriver(IDriverLocationReader reader, MatchingOptions options, TimeProvider clock)
{
    public async Task<MatchResult> ExecuteAsync(GeoPoint pickup, IReadOnlyList<string> excludedDriverIds, CancellationToken cancellationToken)
    {
        if (excludedDriverIds.Count > 100 || excludedDriverIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128))
            throw new MatchingDomainException("Invalid excluded driver IDs.");
        var now = clock.GetUtcNow();
        var since = now.AddSeconds(-options.LocationMaxAgeSeconds);
        var excluded = excludedDriverIds.ToHashSet(StringComparer.Ordinal);
        var locations = await reader.ReadAvailableAsync(since, cancellationToken);
        // shortcut: scan eligible demo drivers; use PostGIS when driver volume requires indexed distance queries.
        var candidate = locations.Where(d => d.IsOnline && d.UpdatedAt >= since && d.UpdatedAt <= now && !excluded.Contains(d.DriverId))
            .Select(d => new DriverCandidate(d.DriverId, pickup.DistanceKmTo(d.Position)))
            .Where(d => d.DistanceKm <= options.RadiusKm)
            .OrderBy(d => d.DistanceKm).ThenBy(d => d.DriverId, StringComparer.Ordinal).FirstOrDefault();
        return new(candidate is null ? "NoDriverAvailable" : "Matched", candidate);
    }
}
