using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftRide.PaymentService.Application.Abstractions;

namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class MockOperationStore(IDbContextFactory<PaymentDbContext> contextFactory) : IMockOperationStore
{
    public async Task<GatewayResult> ExecuteOnceAsync(MockOperation input, CancellationToken ct)
    {
        // 1. Check if receipt already exists in independent context
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            var existing = await db.MockGatewayOperations
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Operation == input.Operation && m.IdempotencyKey == input.IdempotencyKey, ct);

            if (existing is not null)
            {
                if (!string.Equals(existing.RequestHash, input.RequestHash, StringComparison.Ordinal))
                {
                    return GatewayResult.Declined("idempotency_conflict_hash_mismatch");
                }

                return existing.Outcome switch
                {
                    "succeeded" => GatewayResult.Success(existing.Reference!),
                    "declined" => GatewayResult.Declined(existing.FailureCode ?? "declined"),
                    _ => GatewayResult.Transient(existing.FailureCode ?? "transient")
                };
            }
        }

        // 2. Not found, execute action
        var result = await input.Action(ct);

        // 3. Transient errors are NOT persisted as permanent receipts (allowing retry)
        if (result.Outcome == GatewayOutcome.TransientFailure)
        {
            return result;
        }

        // 4. Persist success / declined receipt in independent context
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            var record = new MockGatewayOperationRecord
            {
                Id = Guid.NewGuid(),
                Operation = input.Operation,
                IdempotencyKey = input.IdempotencyKey,
                RequestHash = input.RequestHash,
                Outcome = result.Outcome == GatewayOutcome.Succeeded ? "succeeded" : "declined",
                Reference = result.Reference,
                FailureCode = result.FailureCode,
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.MockGatewayOperations.Add(record);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Concurrent insert won: load winner
                var winner = await db.MockGatewayOperations
                    .AsNoTracking()
                    .FirstAsync(m => m.Operation == input.Operation && m.IdempotencyKey == input.IdempotencyKey, ct);

                return winner.Outcome == "succeeded"
                    ? GatewayResult.Success(winner.Reference!)
                    : GatewayResult.Declined(winner.FailureCode ?? "declined");
            }
        }

        return result;
    }
}

public sealed class InMemoryMockOperationStore : IMockOperationStore
{
    private sealed record StoredReceipt(string RequestHash, GatewayResult Result);
    private readonly ConcurrentDictionary<string, StoredReceipt> _receipts = new();

    public async Task<GatewayResult> ExecuteOnceAsync(MockOperation input, CancellationToken ct)
    {
        var key = $"{input.Operation}:{input.IdempotencyKey}";
        if (_receipts.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.RequestHash, input.RequestHash, StringComparison.Ordinal))
            {
                return GatewayResult.Declined("idempotency_conflict_hash_mismatch");
            }
            return existing.Result;
        }

        var result = await input.Action(ct);
        if (result.Outcome != GatewayOutcome.TransientFailure)
        {
            _receipts.TryAdd(key, new StoredReceipt(input.RequestHash, result));
        }
        return result;
    }
}
