using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Infrastructure.Consumers;

/// <summary>
/// Handles <see cref="DriverMatchRequestedV1"/>: next nearest driver outside the exclusion list, answered with
/// <see cref="DriverMatchedV1"/> or <see cref="DriverMatchFailedV1"/>. Read-only, so it is naturally idempotent.
/// </summary>
public sealed class DriverMatchRequestedConsumer(
    MatchDriver matchDriver,
    TimeProvider clock,
    ILogger<DriverMatchRequestedConsumer> logger) : IConsumer<DriverMatchRequestedV1>
{
    public async Task Consume(ConsumeContext<DriverMatchRequestedV1> context)
    {
        var request = context.Message;
        using var messageScope = logger.BeginScope(
            "EventType={EventType} EventId={EventId} TripId={TripId} CorrelationId={CorrelationId} CausationId={CausationId}",
            nameof(DriverMatchRequestedV1), request.EventId, request.TripId, request.CorrelationId, request.CausationId);
        logger.LogInformation(
            """
            ==================== RABBITMQ MESSAGE RECEIVED [RECEIVED] ====================
            [MESSAGE INFO]
            MessageType        : {EventType}
            EventId            : {EventId}
            TripId             : {TripId}

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
            nameof(DriverMatchRequestedV1),
            request.EventId,
            request.TripId,
            request.CorrelationId,
            request.CausationId,
            context.GetRetryAttempt(),
            context.GetRedeliveryCount(),
            context.MessageId,
            context.SourceAddress,
            context.DestinationAddress,
            context.ReceiveContext?.InputAddress,
            JsonSerializer.Serialize(new
            {
                request.EventId,
                request.TripId,
                request.OccurredAtUtc,
                request.CorrelationId,
                request.CausationId
            }, new JsonSerializerOptions { WriteIndented = true }));
        var ct = context.CancellationToken;
        var excluded = request.ExcludedDriverIds ?? Array.Empty<string>();

        try
        {
            var match = await matchDriver.ExecuteAsync(new GeoPoint(request.PickupLatitude, request.PickupLongitude), excluded, ct);
            if (match.Candidate is null)
            {
                logger.LogInformation("No driver available for trip {TripId} after {Excluded} exclusions", request.TripId, excluded.Count);
                await PublishFailedAsync(context, "NoDriverAvailable");
                return;
            }

            var reply = new DriverMatchedV1(
                EventId: Guid.NewGuid(),
                TripId: request.TripId,
                DriverId: match.Candidate.DriverId,
                DistanceKm: match.Candidate.DistanceKm,
                ExcludedDriverIds: excluded,
                OccurredAtUtc: clock.GetUtcNow(),
                CorrelationId: request.CorrelationId,
                CausationId: request.EventId);
            await context.Publish(reply, publish => TraceHeaders.Forward(context, publish), ct);
            logger.LogInformation(
                """
                ==================== RABBITMQ MESSAGE PUBLISHED [PUBLISHED] ===================
                [MESSAGE INFO]
                MessageType        : {EventType}
                EventId            : {EventId}
                TripId             : {TripId}

                [CORRELATION INFO]
                CorrelationId      : {CorrelationId}
                CausationId        : {CausationId}

                [MESSAGE CONTEXT]
                Exchange           : SwiftRide.Contracts.V1:{ExchangeEventType}
                =============================================================================
                """,
                nameof(DriverMatchedV1), reply.EventId, reply.TripId, reply.CorrelationId, reply.CausationId, nameof(DriverMatchedV1));
            logger.LogInformation("[PROCESSED] {EventType} EventId={EventId} TripId={TripId}", nameof(DriverMatchRequestedV1), request.EventId, request.TripId);
        }
        catch (MatchingDomainException ex)
        {
            logger.LogWarning(ex, "Driver match request for trip {TripId} is invalid", request.TripId);
            await PublishFailedAsync(context, "InvalidRequest");
        }
    }

    private async Task PublishFailedAsync(ConsumeContext<DriverMatchRequestedV1> context, string reasonCode)
    {
        var reply = new DriverMatchFailedV1(
            EventId: Guid.NewGuid(),
            TripId: context.Message.TripId,
            ReasonCode: reasonCode,
            OccurredAtUtc: clock.GetUtcNow(),
            CorrelationId: context.Message.CorrelationId,
            CausationId: context.Message.EventId);
        await context.Publish(reply, publish => TraceHeaders.Forward(context, publish), context.CancellationToken);
        logger.LogInformation(
            """
            ==================== RABBITMQ MESSAGE PUBLISHED [PUBLISHED] ===================
            [MESSAGE INFO]
            MessageType        : {EventType}
            EventId            : {EventId}
            TripId             : {TripId}

            [CORRELATION INFO]
            CorrelationId      : {CorrelationId}
            CausationId        : {CausationId}

            [MESSAGE CONTEXT]
            Exchange           : SwiftRide.Contracts.V1:{ExchangeEventType}

            [DELIVERY INFO]
            ReasonCode         : {ReasonCode}
            =============================================================================
            """,
            nameof(DriverMatchFailedV1), reply.EventId, reply.TripId, reply.CorrelationId, reply.CausationId, nameof(DriverMatchFailedV1), reasonCode);
        logger.LogInformation("[PROCESSED] {EventType} EventId={EventId} TripId={TripId}", nameof(DriverMatchRequestedV1), context.Message.EventId, context.Message.TripId);
    }
}
