using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Infrastructure.Consumers;

/// <summary>
/// Handles <see cref="QuoteRequestedV1"/>: nearest driver + quote, answered with
/// <see cref="QuoteReadyV1"/> or <see cref="QuoteFailedV1"/>.
/// Idempotent under at-least-once delivery: driver search is read-only and the quote id is the request's EventId,
/// so a redelivery maps to the same stored quote. Trip also ignores duplicate replies.
/// </summary>
public sealed class QuoteRequestedConsumer(
    MatchDriver matchDriver,
    CreateQuote createQuote,
    TimeProvider clock,
    ILogger<QuoteRequestedConsumer> logger) : IConsumer<QuoteRequestedV1>
{
    public async Task Consume(ConsumeContext<QuoteRequestedV1> context)
    {
        var request = context.Message;
        using var messageScope = logger.BeginScope(
            "EventType={EventType} EventId={EventId} TripId={TripId} CorrelationId={CorrelationId} CausationId={CausationId}",
            nameof(QuoteRequestedV1), request.EventId, request.TripId, request.CorrelationId, request.CausationId);
        logger.LogInformation(
            """
            ==================== RABBITMQ MESSAGE RECEIVED [RECEIVED] ====================
            [MESSAGE INFO]
            MessageType        : {EventType}
            EventId            : {EventId}
            TripId             : {TripId}

            [BUSINESS INFO]
            Mode               : {Mode}

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
            nameof(QuoteRequestedV1),
            request.EventId,
            request.TripId,
            request.Mode,
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
                request.Mode,
                request.OccurredAtUtc,
                request.CorrelationId,
                request.CausationId
            }, new JsonSerializerOptions { WriteIndented = true }));
        var ct = context.CancellationToken;

        try
        {
            var pickup = new GeoPoint(request.PickupLatitude, request.PickupLongitude);
            var destination = new GeoPoint(request.DestinationLatitude, request.DestinationLongitude);

            var match = await matchDriver.ExecuteAsync(pickup, [], ct, request.VehicleType);
            if (match.Candidate is null)
            {
                logger.LogInformation("No driver available for trip {TripId}", request.TripId);
                await PublishFailedAsync(context, "NoDriverAvailable");
                return;
            }

            var quote = await createQuote.ExecuteAsync(pickup, destination, request.Mode, ct, request.EventId, request.VehicleType);

            var reply = new QuoteReadyV1(
                EventId: Guid.NewGuid(),
                TripId: request.TripId,
                QuoteId: quote.QuoteId,
                Amount: quote.Amount,
                Currency: quote.Currency,
                DistanceKm: quote.DistanceKm,
                EstimatedMinutes: quote.EstimatedMinutes,
                CandidateDriverId: match.Candidate.DriverId,
                CandidateDistanceKm: match.Candidate.DistanceKm,
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
                nameof(QuoteReadyV1), reply.EventId, reply.TripId, reply.CorrelationId, reply.CausationId, nameof(QuoteReadyV1));
            logger.LogInformation("[PROCESSED] {EventType} EventId={EventId} TripId={TripId}", nameof(QuoteRequestedV1), request.EventId, request.TripId);
        }
        catch (MatchingDomainException ex)
        {
            // Permanent: the request itself is invalid (bad coordinates, unsupported mode). Do not retry.
            logger.LogWarning(ex, "Quote request for trip {TripId} is invalid", request.TripId);
            await PublishFailedAsync(context, "InvalidRequest");
        }
    }

    private async Task PublishFailedAsync(ConsumeContext<QuoteRequestedV1> context, string reasonCode)
    {
        var reply = new QuoteFailedV1(
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
            nameof(QuoteFailedV1), reply.EventId, reply.TripId, reply.CorrelationId, reply.CausationId, nameof(QuoteFailedV1), reasonCode);
        logger.LogInformation("[PROCESSED] {EventType} EventId={EventId} TripId={TripId}", nameof(QuoteRequestedV1), context.Message.EventId, context.Message.TripId);
    }
}
