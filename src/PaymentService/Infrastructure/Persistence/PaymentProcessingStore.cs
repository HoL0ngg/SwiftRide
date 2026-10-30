using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Payments.Dtos;

namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentProcessingStore(PaymentDbContext db) : IPaymentProcessingStore
{
    public async Task<PaymentProcessingDto?> GetAsync(Guid paymentId, CancellationToken ct)
    {
        var record = await db.PaymentProcessings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PaymentId == paymentId, ct);

        if (record is null) return null;

        var canRetry = record.AttemptsStarted < 3 && record.Outcome == "pending";
        var requiresAction = record.AttemptsStarted >= 3 && record.Outcome == "pending";

        return new PaymentProcessingDto(
            record.AttemptsStarted,
            record.LeaseUntil,
            canRetry,
            requiresAction);
    }

    public async Task<string?> GetDriverIdAsync(Guid paymentId, CancellationToken ct)
    {
        var record = await db.PaymentProcessings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PaymentId == paymentId, ct);
        return record?.DriverId;
    }

    public Task InsertAsync(Guid paymentId, string requestHash, string driverId, DateTimeOffset now, CancellationToken ct)
    {
        var record = new PaymentProcessingRecord
        {
            PaymentId = paymentId,
            RequestHash = requestHash,
            Version = 2,
            DriverId = driverId,
            AttemptsStarted = 0,
            OwnerId = null,
            LeaseUntil = null,
            Outcome = "pending",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.PaymentProcessings.Add(record);
        return Task.CompletedTask;
    }

    public async Task<ProcessingClaimResult> TryClaimAsync(
        Guid paymentId,
        Guid owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxAttempts,
        CancellationToken ct)
    {
        var record = await db.PaymentProcessings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PaymentId == paymentId, ct);

        if (record is null)
        {
            return new ProcessingClaimResult(
                ClaimStatus.MaxAttemptsExceeded,
                0,
                null,
                null,
                CanRetry: false,
                RequiresAction: true);
        }

        if (!string.Equals(record.Outcome, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessingClaimResult(
                ClaimStatus.AlreadyCompleted,
                record.AttemptsStarted,
                record.LeaseUntil,
                record.DriverId,
                CanRetry: false,
                RequiresAction: false);
        }

        if (record.AttemptsStarted >= maxAttempts)
        {
            return new ProcessingClaimResult(
                ClaimStatus.MaxAttemptsExceeded,
                record.AttemptsStarted,
                record.LeaseUntil,
                record.DriverId,
                CanRetry: false,
                RequiresAction: true);
        }

        if (record.LeaseUntil > now && record.OwnerId.HasValue && record.OwnerId.Value != owner)
        {
            return new ProcessingClaimResult(
                ClaimStatus.CurrentlyLeased,
                record.AttemptsStarted,
                record.LeaseUntil,
                record.DriverId,
                CanRetry: true,
                RequiresAction: false);
        }

        var leaseUntil = now + leaseDuration;
        var rowsAffected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE paydb.payment_processing SET owner_id = {owner}, lease_until = {leaseUntil}, attempts_started = attempts_started + 1, updated_at = {now} WHERE payment_id = {paymentId} AND outcome = 'pending' AND attempts_started < {maxAttempts} AND (lease_until IS NULL OR lease_until <= {now} OR owner_id = {owner})",
            ct);

        if (rowsAffected > 0)
        {
            return new ProcessingClaimResult(
                ClaimStatus.Claimed,
                record.AttemptsStarted + 1,
                leaseUntil,
                record.DriverId,
                CanRetry: true,
                RequiresAction: false);
        }

        // Concurrency collision: reload record
        var fresh = await db.PaymentProcessings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PaymentId == paymentId, ct);

        if (fresh is null || !string.Equals(fresh.Outcome, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessingClaimResult(
                ClaimStatus.AlreadyCompleted,
                fresh?.AttemptsStarted ?? record.AttemptsStarted,
                fresh?.LeaseUntil,
                fresh?.DriverId ?? record.DriverId,
                CanRetry: false,
                RequiresAction: false);
        }

        if (fresh.AttemptsStarted >= maxAttempts)
        {
            return new ProcessingClaimResult(
                ClaimStatus.MaxAttemptsExceeded,
                fresh.AttemptsStarted,
                fresh.LeaseUntil,
                fresh.DriverId,
                CanRetry: false,
                RequiresAction: true);
        }

        return new ProcessingClaimResult(
            ClaimStatus.CurrentlyLeased,
            fresh.AttemptsStarted,
            fresh.LeaseUntil,
            fresh.DriverId,
            CanRetry: true,
            RequiresAction: false);
    }

    public async Task<bool> CompleteAsync(Guid paymentId, Guid owner, string outcome, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rowsAffected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE paydb.payment_processing SET outcome = {outcome}, lease_until = NULL, updated_at = {now} WHERE payment_id = {paymentId} AND (owner_id = {owner} OR owner_id IS NULL)",
            ct);
        return rowsAffected > 0;
    }
}

public sealed class InMemoryPaymentProcessingStore : IPaymentProcessingStore
{
    private sealed record Entry(string RequestHash, string DriverId, int Attempts, Guid? Owner, DateTimeOffset? LeaseUntil, string Outcome);
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public Task<PaymentProcessingDto?> GetAsync(Guid paymentId, CancellationToken ct)
    {
        if (_entries.TryGetValue(paymentId, out var e))
        {
            var canRetry = e.Attempts < 3 && e.Outcome == "pending";
            var requiresAction = e.Attempts >= 3 && e.Outcome == "pending";
            return Task.FromResult<PaymentProcessingDto?>(new PaymentProcessingDto(e.Attempts, e.LeaseUntil, canRetry, requiresAction));
        }
        return Task.FromResult<PaymentProcessingDto?>(null);
    }

    public Task<string?> GetDriverIdAsync(Guid paymentId, CancellationToken ct)
    {
        _entries.TryGetValue(paymentId, out var e);
        return Task.FromResult(e?.DriverId);
    }

    public Task InsertAsync(Guid paymentId, string requestHash, string driverId, DateTimeOffset now, CancellationToken ct)
    {
        _entries[paymentId] = new Entry(requestHash, driverId, 0, null, null, "pending");
        return Task.CompletedTask;
    }

    public Task<ProcessingClaimResult> TryClaimAsync(Guid paymentId, Guid owner, DateTimeOffset now, TimeSpan leaseDuration, int maxAttempts, CancellationToken ct)
    {
        if (!_entries.TryGetValue(paymentId, out var e))
        {
            return Task.FromResult(new ProcessingClaimResult(ClaimStatus.MaxAttemptsExceeded, 0, null, null, false, true));
        }

        if (e.Outcome != "pending")
        {
            return Task.FromResult(new ProcessingClaimResult(ClaimStatus.AlreadyCompleted, e.Attempts, e.LeaseUntil, e.DriverId, false, false));
        }

        if (e.Attempts >= maxAttempts)
        {
            return Task.FromResult(new ProcessingClaimResult(ClaimStatus.MaxAttemptsExceeded, e.Attempts, e.LeaseUntil, e.DriverId, false, true));
        }

        if (e.LeaseUntil > now && e.Owner.HasValue && e.Owner.Value != owner)
        {
            return Task.FromResult(new ProcessingClaimResult(ClaimStatus.CurrentlyLeased, e.Attempts, e.LeaseUntil, e.DriverId, true, false));
        }

        var leaseUntil = now + leaseDuration;
        _entries[paymentId] = e with { Attempts = e.Attempts + 1, Owner = owner, LeaseUntil = leaseUntil };
        return Task.FromResult(new ProcessingClaimResult(ClaimStatus.Claimed, e.Attempts + 1, leaseUntil, e.DriverId, true, false));
    }

    public Task<bool> CompleteAsync(Guid paymentId, Guid owner, string outcome, CancellationToken ct)
    {
        if (_entries.TryGetValue(paymentId, out var e))
        {
            if (e.Owner == owner || e.Owner == null)
            {
                _entries[paymentId] = e with { Outcome = outcome, LeaseUntil = null };
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }
}
