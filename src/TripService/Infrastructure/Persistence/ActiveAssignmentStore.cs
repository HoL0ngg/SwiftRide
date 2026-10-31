using Microsoft.EntityFrameworkCore;
using SwiftRide.TripService.Application.Abstractions;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class ActiveAssignmentStore : IActiveAssignmentStore
{
    private readonly TripDbContext _db;

    public ActiveAssignmentStore(TripDbContext db)
    {
        _db = db;
    }

    public void ClaimRider(string riderId, Guid tripId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(riderId);
        _db.ActiveRiderTrips.Add(new ActiveRiderTripRecord
        {
            RiderId = riderId,
            TripId = tripId
        });
    }

    public void ReleaseRider(Guid tripId)
    {
        var existing = _db.ActiveRiderTrips.Local.Where(a => a.TripId == tripId).ToList();
        if (existing.Count > 0)
        {
            foreach (var r in existing)
            {
                if (_db.Entry(r).State == EntityState.Added)
                {
                    _db.Entry(r).State = EntityState.Detached;
                }
                else
                {
                    _db.ActiveRiderTrips.Remove(r);
                }
            }
        }
        else
        {
            var tracked = _db.ActiveRiderTrips.Where(a => a.TripId == tripId).ToList();
            if (tracked.Count > 0)
            {
                _db.ActiveRiderTrips.RemoveRange(tracked);
            }
        }
    }

    public void ClaimDriver(string driverId, Guid tripId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        _db.ActiveDriverAssignments.Add(new ActiveDriverAssignmentRecord
        {
            DriverId = driverId,
            TripId = tripId
        });
    }

    public bool TryClaimDriver(string driverId, Guid tripId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);

        // Identify any records staged for deletion in the current change tracker
        var stagedDeletions = _db.ChangeTracker.Entries<ActiveDriverAssignmentRecord>()
            .Where(e => e.State == EntityState.Deleted)
            .Select(e => e.Entity)
            .ToList();

        var stagedDeletedDriverIds = stagedDeletions
            .Select(d => d.DriverId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stagedDeletedTripIds = stagedDeletions
            .Select(d => d.TripId)
            .ToHashSet();

        // 1. Check if driver is already claimed locally (excluding Deleted or Detached)
        var driverClaimedLocally = _db.ChangeTracker.Entries<ActiveDriverAssignmentRecord>()
            .Any(e => e.Entity.DriverId.Equals(driverId, StringComparison.OrdinalIgnoreCase) &&
                      e.State != EntityState.Deleted &&
                      e.State != EntityState.Detached);
        if (driverClaimedLocally) return false;

        // 2. Check if driver is claimed in DB (if driver is staged for deletion in this unit of work, they are not busy)
        if (!stagedDeletedDriverIds.Contains(driverId))
        {
            var driverClaimedInDb = _db.ActiveDriverAssignments.Any(a => a.DriverId == driverId);
            if (driverClaimedInDb) return false;
        }

        // 3. Check if trip already has a driver locally (excluding Deleted or Detached)
        var tripClaimedLocally = _db.ChangeTracker.Entries<ActiveDriverAssignmentRecord>()
            .Any(e => e.Entity.TripId == tripId &&
                      e.State != EntityState.Deleted &&
                      e.State != EntityState.Detached);
        if (tripClaimedLocally) return false;

        // 4. Check if trip already has a driver in DB (if old driver for this trip is staged for deletion, slot is being freed)
        if (!stagedDeletedTripIds.Contains(tripId))
        {
            var tripClaimedInDb = _db.ActiveDriverAssignments.Any(a => a.TripId == tripId);
            if (tripClaimedInDb) return false;
        }

        // If this exact driver was staged for deletion, repurpose the tracked entity to avoid duplicate PK tracking
        var existingDeleted = _db.ChangeTracker.Entries<ActiveDriverAssignmentRecord>()
            .FirstOrDefault(e => e.Entity.DriverId.Equals(driverId, StringComparison.OrdinalIgnoreCase) && e.State == EntityState.Deleted);

        if (existingDeleted is not null)
        {
            existingDeleted.Entity.TripId = tripId;
            existingDeleted.State = EntityState.Modified;
            return true;
        }

        var record = new ActiveDriverAssignmentRecord
        {
            DriverId = driverId,
            TripId = tripId
        };

        _db.ActiveDriverAssignments.Add(record);
        return true;
    }

    public void ReleaseDriver(Guid tripId)
    {
        var existing = _db.ActiveDriverAssignments.Local.Where(a => a.TripId == tripId).ToList();
        if (existing.Count > 0)
        {
            foreach (var r in existing)
            {
                if (_db.Entry(r).State == EntityState.Added)
                {
                    _db.Entry(r).State = EntityState.Detached;
                }
                else
                {
                    _db.ActiveDriverAssignments.Remove(r);
                }
            }
        }
        else
        {
            var tracked = _db.ActiveDriverAssignments.Where(a => a.TripId == tripId).ToList();
            if (tracked.Count > 0)
            {
                _db.ActiveDriverAssignments.RemoveRange(tracked);
            }
        }
    }
}
