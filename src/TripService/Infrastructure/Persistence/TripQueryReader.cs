using Microsoft.EntityFrameworkCore;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class TripQueryReader : ITripQueryReader
{
    private readonly TripDbContext _db;

    public TripQueryReader(TripDbContext db)
    {
        _db = db;
    }

    public async Task<TripContext?> GetCurrentAsync(TripActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Guid? activeTripId = null;
        if (actor.IsRider)
        {
            var activeRider = await _db.ActiveRiderTrips.FirstOrDefaultAsync(a => a.RiderId == actor.Id, ct);
            activeTripId = activeRider?.TripId;
        }
        else if (actor.IsDriver)
        {
            var activeDriver = await _db.ActiveDriverAssignments.FirstOrDefaultAsync(a => a.DriverId == actor.Id, ct);
            activeTripId = activeDriver?.TripId;
        }

        if (!activeTripId.HasValue)
        {
            return null;
        }

        var record = await _db.Trips
            .Include(t => t.Rejections)
            .FirstOrDefaultAsync(t => t.Id == activeTripId.Value, ct);

        return record?.ToContext();
    }

    public async Task<DriverFeedReadModel> GetDriverFeedAsync(TripActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsDriver)
        {
            return new DriverFeedReadModel(null, null);
        }

        var offeredRecord = await _db.Trips
            .Include(t => t.Rejections)
            .FirstOrDefaultAsync(t => t.OfferedDriverId == actor.Id && t.Status == nameof(TripStatus.DriverOffered), ct);

        var activeRecord = await _db.Trips
            .Include(t => t.Rejections)
            .FirstOrDefaultAsync(t => t.AssignedDriverId == actor.Id &&
                                      (t.Status == nameof(TripStatus.DriverAccepted) ||
                                       t.Status == nameof(TripStatus.EnRoute) ||
                                       t.Status == nameof(TripStatus.PickedUp)), ct);

        return new DriverFeedReadModel(
            offeredRecord?.ToContext(),
            activeRecord?.ToContext());
    }

    public async Task<TripHistoryPageReadModel> GetHistoryAsync(TripActor actor, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        limit = Math.Clamp(limit, 1, 100);

        IQueryable<TripRecord> query = _db.Trips.Include(t => t.Rejections);

        if (actor.IsRider)
        {
            query = query.Where(t => t.RiderId == actor.Id);
        }
        else if (actor.IsDriver)
        {
            query = query.Where(t => t.AssignedDriverId == actor.Id);
        }
        else
        {
            return new TripHistoryPageReadModel(Array.Empty<TripContext>(), null);
        }

        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var parts = cursor.Split('|');
            if (parts.Length == 2 &&
                DateTimeOffset.TryParse(parts[0], out var parsedTime) &&
                Guid.TryParse(parts[1], out var parsedGuid))
            {
                cursorCreatedAt = parsedTime;
                cursorId = parsedGuid;
            }
        }

        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            query = query.Where(t => t.CreatedAt < cursorCreatedAt.Value ||
                                     (t.CreatedAt == cursorCreatedAt.Value && t.Id.CompareTo(cursorId.Value) < 0));
        }

        query = query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id);

        var records = await query.Take(limit + 1).ToListAsync(ct);

        string? nextCursor = null;
        if (records.Count > limit)
        {
            var lastItem = records[limit - 1];
            nextCursor = $"{lastItem.CreatedAt:O}|{lastItem.Id:D}";
            records = records.Take(limit).ToList();
        }

        var items = records.Select(r => r.ToContext()).ToList();
        return new TripHistoryPageReadModel(items, nextCursor);
    }
}
