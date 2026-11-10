using Microsoft.EntityFrameworkCore;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

/// <summary>
/// Inbox deduplication for the Matching reply consumers. The inbox row is staged in the same
/// <c>SaveChanges</c> as the trip change, so "state changed" and "message processed" commit atomically.
/// A concurrent duplicate hits the (ConsumerName, MessageId) key, fails the save and is retried by the broker,
/// at which point <see cref="IsProcessedAsync"/> reports it as already handled.
/// </summary>
internal static class InboxGate
{
    public static Task<bool> IsProcessedAsync(TripDbContext db, string consumerName, Guid messageId, CancellationToken ct) =>
        db.IntegrationInbox.AnyAsync(
            i => i.ConsumerName == consumerName && i.MessageId == messageId && i.ProcessedAtUtc != null, ct);

    public static void MarkProcessed(TripDbContext db, string consumerName, Guid messageId, TimeProvider clock, string? note = null)
    {
        var now = clock.GetUtcNow();
        db.IntegrationInbox.Add(new IntegrationInboxRecord
        {
            ConsumerName = consumerName,
            MessageId = messageId,
            ReceivedAtUtc = now,
            ProcessedAtUtc = now,
            LastError = note
        });
    }
}
