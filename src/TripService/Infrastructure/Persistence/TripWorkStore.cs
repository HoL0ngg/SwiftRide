using Microsoft.EntityFrameworkCore;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class TripWorkStore : ITripWorkStore
{
    private readonly TripDbContext _db;

    public TripWorkStore(TripDbContext db)
    {
        _db = db;
    }

    public async Task<TripWorkLease?> TryClaimAsync(TripWorkKind kind, Guid owner, DateTimeOffset now, CancellationToken ct)
    {
        if (kind == TripWorkKind.Payment)
        {
            var candidate = await _db.TripPaymentJobs
                .AsNoTracking()
                .Where(j => j.Status == "Pending" &&
                            !j.RequiresAction &&
                            j.Transport != "Events" &&
                            j.NextAttemptUtc <= now &&
                            (j.Owner == null || j.LeaseUntilUtc == null || j.LeaseUntilUtc < now))
                .OrderBy(j => j.NextAttemptUtc)
                .FirstOrDefaultAsync(ct);

            if (candidate is null)
            {
                return null;
            }

            var leaseUntil = now.AddSeconds(30);

            if (_db.Database.IsRelational())
            {
                var rows = await _db.TripPaymentJobs
                    .Where(j => j.TripId == candidate.TripId &&
                                j.Status == "Pending" &&
                                !j.RequiresAction &&
                                j.Transport != "Events" &&
                                (j.Owner == null || j.LeaseUntilUtc == null || j.LeaseUntilUtc < now))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Owner, owner)
                        .SetProperty(j => j.LeaseUntilUtc, leaseUntil)
                        .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                        .SetProperty(j => j.UpdatedAt, now), ct);

                if (rows == 0)
                {
                    return null; // CAS conflict: lost race to another worker
                }

                var local = _db.TripPaymentJobs.Local.FirstOrDefault(j => j.TripId == candidate.TripId);
                if (local is not null)
                {
                    _db.Entry(local).State = EntityState.Detached;
                }
            }
            else
            {
                var tracked = await _db.TripPaymentJobs.FirstOrDefaultAsync(j => j.TripId == candidate.TripId, ct);
                if (tracked is null || tracked.Status != "Pending" || tracked.RequiresAction ||
                    (tracked.Owner != null && tracked.LeaseUntilUtc >= now))
                {
                    return null;
                }
                tracked.Owner = owner;
                tracked.LeaseUntilUtc = leaseUntil;
                tracked.Attempts++;
                tracked.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
            }

            return new TripWorkLease(
                TripWorkKind.Payment,
                candidate.TripId,
                candidate.TripId,
                owner,
                leaseUntil,
                candidate.Attempts + 1,
                candidate.MaxAttempts,
                candidate.DeadlineUtc,
                candidate.Transport);
        }

        if (kind == TripWorkKind.Matching)
        {
            var candidateReceipt = await _db.CommandReceipts
                .AsNoTracking()
                .Where(r => r.Scope.Contains("create") &&
                            r.Status == "pending" &&
                            (r.NextAttemptUtc == null || r.NextAttemptUtc <= now))
                .OrderBy(r => r.NextAttemptUtc ?? r.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (candidateReceipt is null || string.IsNullOrWhiteSpace(candidateReceipt.ResponseJson) ||
                !Guid.TryParse(candidateReceipt.ResponseJson, out var tripId))
            {
                return null;
            }

            var trip = await _db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, ct);
            if (trip is null || trip.Status != "Requested")
            {
                // Trip is no longer Requested: complete stale receipt
                var trackedReceipt = await _db.CommandReceipts.FirstOrDefaultAsync(r => r.Scope == candidateReceipt.Scope && r.Key == candidateReceipt.Key, ct);
                if (trackedReceipt is not null && trackedReceipt.Status == "pending")
                {
                    trackedReceipt.Status = "completed";
                    trackedReceipt.UpdatedAt = now;
                    await _db.SaveChangesAsync(ct);
                }
                return null;
            }

            var leaseUntil = now.AddSeconds(30);

            if (_db.Database.IsRelational())
            {
                var rows = await _db.CommandReceipts
                    .Where(r => r.Scope == candidateReceipt.Scope &&
                                r.Key == candidateReceipt.Key &&
                                r.Status == "pending" &&
                                (r.NextAttemptUtc == null || r.NextAttemptUtc <= now))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.NextAttemptUtc, leaseUntil)
                        .SetProperty(r => r.Attempts, r => r.Attempts + 1)
                        .SetProperty(r => r.UpdatedAt, now), ct);

                if (rows == 0)
                {
                    return null;
                }

                var localReceipt = _db.CommandReceipts.Local.FirstOrDefault(r => r.Scope == candidateReceipt.Scope && r.Key == candidateReceipt.Key);
                if (localReceipt is not null)
                {
                    _db.Entry(localReceipt).State = EntityState.Detached;
                }
            }
            else
            {
                var trackedReceipt = await _db.CommandReceipts.FirstOrDefaultAsync(r => r.Scope == candidateReceipt.Scope && r.Key == candidateReceipt.Key, ct);
                if (trackedReceipt is null || trackedReceipt.Status != "pending" || (trackedReceipt.NextAttemptUtc != null && trackedReceipt.NextAttemptUtc > now))
                {
                    return null;
                }
                trackedReceipt.NextAttemptUtc = leaseUntil;
                trackedReceipt.Attempts++;
                trackedReceipt.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
            }

            return new TripWorkLease(
                TripWorkKind.Matching,
                tripId,
                tripId,
                owner,
                leaseUntil,
                candidateReceipt.Attempts + 1);
        }

        return null;
    }

    public void StageCompletion(TripWorkLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (lease.Kind == TripWorkKind.Payment)
        {
            var job = _db.TripPaymentJobs.Local.FirstOrDefault(j => j.TripId == lease.TripId)
                   ?? _db.TripPaymentJobs.FirstOrDefault(j => j.TripId == lease.TripId);

            if (job is not null)
            {
                if (job.Owner is not null && job.Owner != lease.Owner)
                {
                    return;
                }

                _db.Entry(job).Property(j => j.Owner).OriginalValue = lease.Owner;
                job.Status = "Completed";
                job.Owner = null;
                job.LeaseUntilUtc = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (lease.Kind == TripWorkKind.Matching)
        {
            var tripIdStr = lease.TripId.ToString("D");
            var receipt = _db.CommandReceipts.Local.FirstOrDefault(r => r.ResponseJson == tripIdStr)
                       ?? _db.CommandReceipts.FirstOrDefault(r => r.ResponseJson == tripIdStr);
            if (receipt is not null)
            {
                receipt.Status = "completed";
                receipt.NextAttemptUtc = null;
                receipt.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public void StageRetry(TripWorkLease lease, DateTimeOffset nextAttempt, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (lease.Kind == TripWorkKind.Payment)
        {
            var job = _db.TripPaymentJobs.Local.FirstOrDefault(j => j.TripId == lease.TripId)
                   ?? _db.TripPaymentJobs.FirstOrDefault(j => j.TripId == lease.TripId);

            if (job is not null)
            {
                if (job.Owner is not null && job.Owner != lease.Owner)
                {
                    return;
                }

                _db.Entry(job).Property(j => j.Owner).OriginalValue = lease.Owner;
                var requiresAction = job.Attempts >= job.MaxAttempts || nextAttempt > job.DeadlineUtc;
                job.NextAttemptUtc = nextAttempt;
                job.Owner = null;
                job.LeaseUntilUtc = null;
                job.RequiresAction = requiresAction;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (lease.Kind == TripWorkKind.Matching)
        {
            var tripIdStr = lease.TripId.ToString("D");
            var receipt = _db.CommandReceipts.Local.FirstOrDefault(r => r.ResponseJson == tripIdStr)
                       ?? _db.CommandReceipts.FirstOrDefault(r => r.ResponseJson == tripIdStr);
            if (receipt is not null)
            {
                receipt.NextAttemptUtc = nextAttempt;
                receipt.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public void StageRequiresAction(TripWorkLease lease, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (lease.Kind == TripWorkKind.Payment)
        {
            var job = _db.TripPaymentJobs.Local.FirstOrDefault(j => j.TripId == lease.TripId)
                   ?? _db.TripPaymentJobs.FirstOrDefault(j => j.TripId == lease.TripId);

            if (job is not null)
            {
                if (job.Owner is not null && job.Owner != lease.Owner)
                {
                    return;
                }

                _db.Entry(job).Property(j => j.Owner).OriginalValue = lease.Owner;
                job.RequiresAction = true;
                job.Owner = null;
                job.LeaseUntilUtc = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (lease.Kind == TripWorkKind.Matching)
        {
            var tripIdStr = lease.TripId.ToString("D");
            var receipt = _db.CommandReceipts.Local.FirstOrDefault(r => r.ResponseJson == tripIdStr)
                       ?? _db.CommandReceipts.FirstOrDefault(r => r.ResponseJson == tripIdStr);
            if (receipt is not null)
            {
                receipt.Status = "failed";
                receipt.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public void AddPaymentJob(Guid tripId, string paymentKey, DateTimeOffset now, int maxAttempts, DateTimeOffset deadlineUtc, string transport)
    {
        _db.TripPaymentJobs.Add(new TripPaymentJobRecord
        {
            TripId = tripId,
            PaymentKey = paymentKey,
            Attempts = 0,
            MaxAttempts = maxAttempts,
            DeadlineUtc = deadlineUtc,
            NextAttemptUtc = now,
            Status = "Pending",
            RequiresAction = false,
            Transport = transport,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public void StageUnownedCompletion(Guid tripId)
    {
        var job = _db.TripPaymentJobs.Local.FirstOrDefault(j => j.TripId == tripId)
               ?? _db.TripPaymentJobs.FirstOrDefault(j => j.TripId == tripId);

        if (job is not null)
        {
            _db.Entry(job).Property(j => j.Owner).OriginalValue = null;
            job.Status = "Completed";
            job.Owner = null;
            job.LeaseUntilUtc = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void StageUnownedRetry(Guid tripId, DateTimeOffset nextAttempt, string reasonCode)
    {
        var job = _db.TripPaymentJobs.Local.FirstOrDefault(j => j.TripId == tripId)
               ?? _db.TripPaymentJobs.FirstOrDefault(j => j.TripId == tripId);

        if (job is not null)
        {
            _db.Entry(job).Property(j => j.Owner).OriginalValue = null;
            job.Attempts++;
            var requiresAction = job.Attempts >= job.MaxAttempts || nextAttempt > job.DeadlineUtc;
            job.NextAttemptUtc = nextAttempt;
            job.Owner = null;
            job.LeaseUntilUtc = null;
            job.RequiresAction = requiresAction;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void StageUnownedRequiresAction(Guid tripId, string reasonCode)
    {
        var job = _db.TripPaymentJobs.Local.FirstOrDefault(j => j.TripId == tripId)
               ?? _db.TripPaymentJobs.FirstOrDefault(j => j.TripId == tripId);

        if (job is not null)
        {
            _db.Entry(job).Property(j => j.Owner).OriginalValue = null;
            job.RequiresAction = true;
            job.Owner = null;
            job.LeaseUntilUtc = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
