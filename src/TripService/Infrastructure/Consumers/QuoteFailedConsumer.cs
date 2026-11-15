using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

/// <summary>Matching could not quote the trip (no driver / invalid request): <c>Requested -> Failed</c>.</summary>
public sealed class QuoteFailedConsumer(
    ITripReader reader,
    ITripWriter writer,
    ITripUnitOfWork unitOfWork,
    IActiveAssignmentStore assignmentStore,
    TripDbContext db,
    TimeProvider clock,
    ILogger<QuoteFailedConsumer> logger) : IConsumer<QuoteFailedV1>
{
    private static readonly string ConsumerName = typeof(QuoteFailedConsumer).FullName!;

    public async Task Consume(ConsumeContext<QuoteFailedV1> context)
    {
        var message = context.Message;
        using var messageScope = logger.BeginScope(
            "EventType={EventType} EventId={EventId} TripId={TripId} CorrelationId={CorrelationId} CausationId={CausationId}",
            nameof(QuoteFailedV1), message.EventId, message.TripId, message.CorrelationId, message.CausationId);
        logger.LogInformation(
            """
            ==================== RABBITMQ MESSAGE RECEIVED [RECEIVED] ====================
            [MESSAGE INFO]
            MessageType        : {EventType}
            EventId            : {EventId}
            TripId             : {TripId}

            [BUSINESS INFO]
            ReasonCode         : {ReasonCode}

            [CORRELATION INFO]
            CorrelationId      : {CorrelationId}
            CausationId        : {CausationId}

            [RETRY INFO]
            RetryAttempt       : {RetryAttempt}
            RedeliveryCount    : {RedeliveryCount}

            [MESSAGE CONTEXT]
            TransportMessageId : {TransportMessageId}
            Source             : {SourceAddress}
            Destination        : {DestinationAddress}
            InputAddress       : {InputAddress}

            [CONTEXT.MESSAGE]
            {ContextMessage}
            =============================================================================
            """,
            nameof(QuoteFailedV1),
            message.EventId,
            message.TripId,
            message.ReasonCode,
            message.CorrelationId,
            message.CausationId,
            context.GetRetryAttempt(),
            context.GetRedeliveryCount(),
            context.MessageId,
            context.SourceAddress,
            context.DestinationAddress,
            context.ReceiveContext?.InputAddress,
            JsonSerializer.Serialize(new
            {
                message.EventId,
                message.TripId,
                message.ReasonCode,
                message.OccurredAtUtc,
                message.CorrelationId,
                message.CausationId
            }, new JsonSerializerOptions { WriteIndented = true }));
        var ct = context.CancellationToken;

        if (await InboxGate.IsProcessedAsync(db, ConsumerName, message.EventId, ct))
        {
            logger.LogInformation("QuoteFailedV1 {EventId} for trip {TripId} already processed; skipping duplicate", message.EventId, message.TripId);
            return;
        }

        var ctx = await reader.LoadAsync(message.TripId, ct);
        string? note = null;

        if (ctx is null)
        {
            logger.LogWarning("QuoteFailedV1 received for unknown trip {TripId}", message.TripId);
            note = "TripNotFound";
        }
        else if (ctx.Trip.Status != TripStatus.Requested)
        {
            logger.LogInformation("QuoteFailedV1 ignored for trip {TripId} in status {Status}", message.TripId, ctx.Trip.Status);
            note = $"IgnoredInStatus:{ctx.Trip.Status}";
        }
        else
        {
            ctx.Trip.FailNoDriverAvailable();
            assignmentStore.ReleaseRider(ctx.Trip.Id);
            ctx.UpdatedAt = clock.GetUtcNow();
            writer.Update(ctx);
            logger.LogInformation("Trip {TripId} failed matching via event: {Reason}", message.TripId, message.ReasonCode);
        }

        InboxGate.MarkProcessed(db, ConsumerName, message.EventId, clock, note);
        await unitOfWork.SaveAsync(ct);
        logger.LogInformation("[PROCESSED] {EventType} EventId={EventId} TripId={TripId}", nameof(QuoteFailedV1), message.EventId, message.TripId);
    }
}
