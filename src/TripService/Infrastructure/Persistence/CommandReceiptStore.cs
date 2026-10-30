using Microsoft.EntityFrameworkCore;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class CommandReceiptStore : ICommandReceiptStore
{
    private readonly TripDbContext _db;

    public CommandReceiptStore(TripDbContext db)
    {
        _db = db;
    }

    public async Task<CommandReceipt?> FindAsync(CommandScope scope, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var scopeStr = scope.ToScopeString();
        var record = await _db.CommandReceipts.FirstOrDefaultAsync(c => c.Scope == scopeStr && c.Key == key, ct);
        if (record is null) return null;

        return new CommandReceipt(
            record.Scope,
            record.Key,
            record.RequestHash,
            record.ResponseJson,
            record.Status,
            record.Attempts,
            record.NextAttemptUtc,
            record.CreatedAt,
            record.UpdatedAt);
    }

    public void Add(CommandReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var existing = _db.CommandReceipts.Local.FirstOrDefault(c => c.Scope == receipt.Scope && c.Key == receipt.Key);
        if (existing is not null)
        {
            existing.RequestHash = receipt.RequestHash;
            existing.ResponseJson = receipt.ResponseJson;
            existing.Status = receipt.Status;
            existing.Attempts = receipt.Attempts;
            existing.NextAttemptUtc = receipt.NextAttemptUtc;
            existing.CreatedAt = receipt.CreatedAt;
            existing.UpdatedAt = receipt.UpdatedAt;
            return;
        }

        _db.CommandReceipts.Add(new CommandReceiptRecord
        {
            Scope = receipt.Scope,
            Key = receipt.Key,
            RequestHash = receipt.RequestHash,
            ResponseJson = receipt.ResponseJson,
            Status = receipt.Status,
            Attempts = receipt.Attempts,
            NextAttemptUtc = receipt.NextAttemptUtc,
            CreatedAt = receipt.CreatedAt,
            UpdatedAt = receipt.UpdatedAt
        });
    }

    public void Update(CommandReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var record = _db.CommandReceipts.Local.FirstOrDefault(c => c.Scope == receipt.Scope && c.Key == receipt.Key);
        if (record is null)
        {
            record = new CommandReceiptRecord { Scope = receipt.Scope, Key = receipt.Key };
            _db.CommandReceipts.Attach(record);
        }

        record.RequestHash = receipt.RequestHash;
        record.ResponseJson = receipt.ResponseJson;
        record.Status = receipt.Status;
        record.Attempts = receipt.Attempts;
        record.NextAttemptUtc = receipt.NextAttemptUtc;
        record.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
