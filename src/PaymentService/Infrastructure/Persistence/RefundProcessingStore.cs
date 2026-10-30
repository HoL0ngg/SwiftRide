using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Application.Abstractions;

namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class RefundProcessingStore(PaymentDbContext db) : IRefundProcessingStore
{
    public Task InsertAsync(Guid refundId, Guid paymentId, Guid tripId, string paymentKey, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        var record = new RefundProcessingRecord
        {
            RefundId = refundId,
            PaymentId = paymentId,
            TripId = tripId,
            PaymentKey = paymentKey,
            Reason = reason,
            AttemptsStarted = 0,
            OwnerId = null,
            LeaseUntil = null,
            Outcome = "pending",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.RefundProcessings.Add(record);
        return Task.CompletedTask;
    }

    public async Task<RefundProcessingClaimResult> TryClaimAsync(
        Guid refundId,
        Guid owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxAttempts,
        CancellationToken ct)
    {
        var record = await db.RefundProcessings
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RefundId == refundId, ct);

        if (record is null)
        {
            return new RefundProcessingClaimResult(
                ClaimStatus.MaxAttemptsExceeded,
                0,
                null,
                CanRetry: false,
                RequiresAction: true);
        }

        if (!string.Equals(record.Outcome, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return new RefundProcessingClaimResult(
                ClaimStatus.AlreadyCompleted,
                record.AttemptsStarted,
                record.LeaseUntil,
                CanRetry: false,
                RequiresAction: false);
        }

        if (record.AttemptsStarted >= maxAttempts)
        {
            return new RefundProcessingClaimResult(
                ClaimStatus.MaxAttemptsExceeded,
                record.AttemptsStarted,
                record.LeaseUntil,
                CanRetry: false,
                RequiresAction: true);
        }

        if (record.OwnerId is not null && record.LeaseUntil > now)
        {
            return new RefundProcessingClaimResult(
                ClaimStatus.CurrentlyLeased,
                record.AttemptsStarted,
                record.LeaseUntil,
                CanRetry: true,
                RequiresAction: false);
        }

        var leaseUntil = now.Add(leaseDuration);

        if (db.Database.IsRelational())
        {
            var rows = await db.RefundProcessings
                .Where(r => r.RefundId == refundId &&
                            r.Outcome == "pending" &&
                            r.AttemptsStarted < maxAttempts &&
                            (r.OwnerId == null || r.LeaseUntil == null || r.LeaseUntil <= now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.OwnerId, owner)
                    .SetProperty(r => r.LeaseUntil, leaseUntil)
                    .SetProperty(r => r.AttemptsStarted, r => r.AttemptsStarted + 1)
                    .SetProperty(r => r.UpdatedAt, now), ct);

            if (rows == 0)
            {
                var fresh = await db.RefundProcessings.AsNoTracking().FirstOrDefaultAsync(r => r.RefundId == refundId, ct);
                if (fresh is null || fresh.Outcome != "pending")
                {
                    return new RefundProcessingClaimResult(ClaimStatus.AlreadyCompleted, fresh?.AttemptsStarted ?? 0, fresh?.LeaseUntil, false, false);
                }
                if (fresh.AttemptsStarted >= maxAttempts)
                {
                    return new RefundProcessingClaimResult(ClaimStatus.MaxAttemptsExceeded, fresh.AttemptsStarted, fresh.LeaseUntil, false, true);
                }
                return new RefundProcessingClaimResult(ClaimStatus.CurrentlyLeased, fresh.AttemptsStarted, fresh.LeaseUntil, true, false);
            }

            var local = db.RefundProcessings.Local.FirstOrDefault(r => r.RefundId == refundId);
            if (local is not null)
            {
                db.Entry(local).State = EntityState.Detached;
            }
        }
        else
        {
            var tracked = await db.RefundProcessings.FirstOrDefaultAsync(r => r.RefundId == refundId, ct);
            if (tracked is null || tracked.Outcome != "pending" || tracked.AttemptsStarted >= maxAttempts ||
                (tracked.OwnerId != null && tracked.LeaseUntil > now))
            {
                return new RefundProcessingClaimResult(ClaimStatus.CurrentlyLeased, tracked?.AttemptsStarted ?? 0, tracked?.LeaseUntil, true, false);
            }

            tracked.OwnerId = owner;
            tracked.LeaseUntil = leaseUntil;
            tracked.AttemptsStarted++;
            tracked.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return new RefundProcessingClaimResult(
            ClaimStatus.Claimed,
            record.AttemptsStarted + 1,
            leaseUntil,
            CanRetry: true,
            RequiresAction: false);
    }

    public async Task<bool> CompleteAsync(Guid refundId, Guid owner, string outcome, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (db.Database.IsRelational())
        {
            var rows = await db.RefundProcessings
                .Where(r => r.RefundId == refundId && (r.OwnerId == owner || r.OwnerId == null || r.OwnerId == Guid.Empty))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Outcome, outcome)
                    .SetProperty(r => r.OwnerId, (Guid?)null)
                    .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(r => r.UpdatedAt, now), ct);

            var local = db.RefundProcessings.Local.FirstOrDefault(r => r.RefundId == refundId);
            if (local is not null)
            {
                db.Entry(local).State = EntityState.Detached;
            }

            return rows > 0;
        }

        var tracked = await db.RefundProcessings.FirstOrDefaultAsync(r => r.RefundId == refundId, ct);
        if (tracked is null) return false;
        if (tracked.OwnerId != owner && tracked.OwnerId != null && tracked.OwnerId != Guid.Empty) return false;

        tracked.Outcome = outcome;
        tracked.OwnerId = null;
        tracked.LeaseUntil = null;
        tracked.UpdatedAt = now;
        return true;
    }
}
