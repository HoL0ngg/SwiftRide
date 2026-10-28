using Microsoft.EntityFrameworkCore;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Infrastructure.Persistence;

public sealed class MatchingRepository(MatchingDbContext db) : IDriverLocationReader, IDriverLocationWriter, IQuoteWriter
{
    public async Task<IReadOnlyList<DriverLocation>> ReadAvailableAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var rows = await db.DriverLocations.AsNoTracking().Where(d => d.IsOnline && d.UpdatedAt >= since).ToListAsync(cancellationToken);
        return rows.Select(d => new DriverLocation(d.DriverId, new GeoPoint(d.Latitude, d.Longitude), d.IsOnline, d.UpdatedAt)).ToArray();
    }
    public async Task UpsertAsync(DriverLocation location, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO matchdb.driver_locations (driver_id, latitude, longitude, is_online, updated_at)
            VALUES ({location.DriverId}, {location.Position.Latitude}, {location.Position.Longitude}, {location.IsOnline}, {location.UpdatedAt})
            ON CONFLICT (driver_id) DO UPDATE SET latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude,
                is_online = EXCLUDED.is_online, updated_at = EXCLUDED.updated_at
            WHERE driver_locations.updated_at <= EXCLUDED.updated_at
            """, cancellationToken);
    }
    public async Task AddAsync(QuoteSnapshot quote, CancellationToken cancellationToken)
    {
        var p = quote.Pricing;
        db.Quotes.Add(new QuoteRow { Id = quote.Id, DistanceKm = p.DistanceKm, EstimatedMinutes = p.EstimatedMinutes,
            CostPerKm = p.CostPerKm, CostPerMinute = p.CostPerMinute, Surge = p.Surge, Toll = p.Toll,
            PromotionFactor = p.PromotionFactor, Tax = p.Tax, Amount = quote.Amount, Currency = quote.Currency, CreatedAt = quote.CreatedAt });
        await db.SaveChangesAsync(cancellationToken);
    }
}
