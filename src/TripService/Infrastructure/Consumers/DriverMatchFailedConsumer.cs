using MassTransit;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

/// <summary>Matching has no more drivers for a trip waiting for an offer: <c>DriverOffered (rematch pending) -> Failed</c>.</summary>
public sealed class DriverMatchFailedConsumer(
    ITripReader reader,
    ITripWriter writer,
    ITripUnitOfWork unitOfWork,
    IActiveAssignmentStore assignmentStore,
    TripDbContext db,
    TimeProvider clock,
    ILogger<DriverMatchFailedConsumer> logger) : IConsumer<DriverMatchFailedV1>
{
    private static readonly string ConsumerName = typeof(DriverMatchFailedConsumer).FullName!;

    public async Task Consume(ConsumeContext<DriverMatchFailedV1> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        if (await InboxGate.IsProcessedAsync(db, ConsumerName, message.EventId, ct))
        {
            logger.LogInformation("DriverMatchFailedV1 {EventId} for trip {TripId} already processed; skipping duplicate", message.EventId, message.TripId);
            return;
        }

        var ctx = await reader.LoadAsync(message.TripId, ct);
        string? note = null;

        if (ctx is null)
        {
            logger.LogWarning("DriverMatchFailedV1 received for unknown trip {TripId}", message.TripId);
            note = "TripNotFound";
        }
        else if (ctx.Trip.Status != TripStatus.DriverOffered || !ctx.Trip.IsRematchPending)
        {
            logger.LogInformation("DriverMatchFailedV1 ignored for trip {TripId} in status {Status}", message.TripId, ctx.Trip.Status);
            note = $"IgnoredInStatus:{ctx.Trip.Status}";
        }
        else
        {
            ctx.Trip.FailNoDriverAvailable();
            assignmentStore.ReleaseRider(ctx.Trip.Id);
            ctx.UpdatedAt = clock.GetUtcNow();
            writer.Update(ctx);
            logger.LogInformation("Trip {TripId} failed: no driver after rematch ({Reason})", message.TripId, message.ReasonCode);
        }

        InboxGate.MarkProcessed(db, ConsumerName, message.EventId, clock, note);
        await unitOfWork.SaveAsync(ct);
    }
}
