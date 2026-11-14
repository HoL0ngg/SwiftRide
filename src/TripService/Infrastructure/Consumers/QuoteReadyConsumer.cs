using MassTransit;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

/// <summary>Matching found a driver and priced the trip: <c>Requested -> Quoted</c>.</summary>
public sealed class QuoteReadyConsumer(
    ITripReader reader,
    ITripWriter writer,
    ITripUnitOfWork unitOfWork,
    IActiveAssignmentStore assignmentStore,
    TripDbContext db,
    TimeProvider clock,
    ILogger<QuoteReadyConsumer> logger) : IConsumer<QuoteReadyV1>
{
    private static readonly string ConsumerName = typeof(QuoteReadyConsumer).FullName!;

    public async Task Consume(ConsumeContext<QuoteReadyV1> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        if (await InboxGate.IsProcessedAsync(db, ConsumerName, message.EventId, ct))
        {
            logger.LogInformation("QuoteReadyV1 {EventId} for trip {TripId} already processed; skipping duplicate", message.EventId, message.TripId);
            return;
        }

        var ctx = await reader.LoadAsync(message.TripId, ct);
        if (ctx is null)
        {
            logger.LogWarning("QuoteReadyV1 received for unknown trip {TripId}", message.TripId);
            await CompleteAsync(message, "TripNotFound", ct);
            return;
        }

        if (ctx.Trip.Status != TripStatus.Requested)
        {
            // Stale or duplicate reply (trip already quoted, cancelled or failed).
            logger.LogInformation("QuoteReadyV1 ignored for trip {TripId} in status {Status}", message.TripId, ctx.Trip.Status);
            await CompleteAsync(message, $"IgnoredInStatus:{ctx.Trip.Status}", ct);
            return;
        }

        try
        {
            ctx.Trip.ApplyQuote(new FareSnapshot(message.QuoteId, message.Amount, message.Currency), message.CandidateDriverId);
            ctx.DistanceKm = message.DistanceKm;
            ctx.EstimatedMinutes = message.EstimatedMinutes;
        }
        catch (TripDomainException ex)
        {
            // Permanent: Matching sent a quote Trip cannot accept. Failing the trip is better than leaving it Requested forever.
            logger.LogError(ex, "Trip {TripId} rejected quote {QuoteId}; marking trip failed", message.TripId, message.QuoteId);
            ctx.Trip.FailNoDriverAvailable();
            assignmentStore.ReleaseRider(ctx.Trip.Id);
        }

        ctx.UpdatedAt = clock.GetUtcNow();
        writer.Update(ctx);
        await CompleteAsync(message, null, ct);
        logger.LogInformation("Trip {TripId} quoted via event: driver {DriverId}, fare {Fare} {Currency}",
            message.TripId, message.CandidateDriverId, message.Amount, message.Currency);
    }

    private Task CompleteAsync(QuoteReadyV1 message, string? note, CancellationToken ct)
    {
        InboxGate.MarkProcessed(db, ConsumerName, message.EventId, clock, note);
        return unitOfWork.SaveAsync(ct);
    }
}
