using MassTransit;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Messaging;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

/// <summary>
/// Matching found a driver for a trip waiting for an offer (<c>DriverOffered</c>, rematch pending).
/// The driver is claimed atomically; if the driver is busy, another match is requested with a longer exclusion list.
/// A concurrent claim conflict surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>,
/// which is retried by the broker in a fresh scope (the busy driver is then detected by <c>TryClaimDriver</c>).
/// </summary>
public sealed class DriverMatchedConsumer(
    ITripReader reader,
    ITripWriter writer,
    ITripUnitOfWork unitOfWork,
    IActiveAssignmentStore assignmentStore,
    ITripEventWriter eventWriter,
    TripDbContext db,
    TimeProvider clock,
    ILogger<DriverMatchedConsumer> logger) : IConsumer<DriverMatchedV1>
{
    private const int MaxExcludedDrivers = 100;
    private static readonly string ConsumerName = typeof(DriverMatchedConsumer).FullName!;

    public async Task Consume(ConsumeContext<DriverMatchedV1> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        if (await InboxGate.IsProcessedAsync(db, ConsumerName, message.EventId, ct))
        {
            logger.LogInformation("DriverMatchedV1 {EventId} for trip {TripId} already processed; skipping duplicate", message.EventId, message.TripId);
            return;
        }

        var ctx = await reader.LoadAsync(message.TripId, ct);
        if (ctx is null)
        {
            logger.LogWarning("DriverMatchedV1 received for unknown trip {TripId}", message.TripId);
            await CompleteAsync(message, "TripNotFound", ct);
            return;
        }

        var trip = ctx.Trip;
        if (trip.Status != TripStatus.DriverOffered || !trip.IsRematchPending)
        {
            // Stale or duplicate reply: trip was cancelled, already has an offer, or moved on.
            logger.LogInformation("DriverMatchedV1 ignored for trip {TripId} in status {Status} (rematchPending={Pending})",
                message.TripId, trip.Status, trip.IsRematchPending);
            await CompleteAsync(message, $"IgnoredInStatus:{trip.Status}", ct);
            return;
        }

        var now = clock.GetUtcNow();
        var rejected = trip.RejectedDriverIds;
        var alreadyRejected = rejected.Contains(message.DriverId, StringComparer.Ordinal);

        if (!alreadyRejected && assignmentStore.TryClaimDriver(message.DriverId, trip.Id))
        {
            trip.OfferReplacementDriver(message.DriverId);
            ctx.UpdatedAt = now;
            writer.Update(ctx);
            await CompleteAsync(message, null, ct);
            logger.LogInformation("Trip {TripId} offered to driver {DriverId} via event", trip.Id, message.DriverId);
            return;
        }

        // Driver is busy (or was already rejected): ask for the next one, excluding everyone seen so far.
        var excluded = (message.ExcludedDriverIds ?? Array.Empty<string>())
            .Concat(rejected)
            .Append(message.DriverId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var busyCount = excluded.Count(id => !rejected.Contains(id, StringComparer.Ordinal));

        if (busyCount > trip.MaxDriverRejections || excluded.Count > MaxExcludedDrivers)
        {
            trip.FailNoDriverAvailable();
            assignmentStore.ReleaseRider(trip.Id);
            ctx.UpdatedAt = now;
            writer.Update(ctx);
            await CompleteAsync(message, "TooManyBusyDrivers", ct);
            logger.LogInformation("Trip {TripId}: too many busy drivers ({Count}), trip failed", trip.Id, busyCount);
            return;
        }

        logger.LogInformation("Trip {TripId} driver {DriverId} is busy; requesting another match", trip.Id, message.DriverId);
        eventWriter.Add(new DriverMatchRequestedV1(
            EventId: Guid.NewGuid(),
            TripId: trip.Id,
            PickupLatitude: ctx.PickupLat,
            PickupLongitude: ctx.PickupLon,
            ExcludedDriverIds: excluded,
            OccurredAtUtc: now,
            CorrelationId: message.CorrelationId,
            CausationId: message.EventId));
        await CompleteAsync(message, "DriverBusy", ct);
    }

    private Task CompleteAsync(DriverMatchedV1 message, string? note, CancellationToken ct)
    {
        InboxGate.MarkProcessed(db, ConsumerName, message.EventId, clock, note);
        return unitOfWork.SaveAsync(ct);
    }
}
