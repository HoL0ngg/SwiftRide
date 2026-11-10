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
        var ct = context.CancellationToken;

        try
        {
            var pickup = new GeoPoint(request.PickupLatitude, request.PickupLongitude);
            var destination = new GeoPoint(request.DestinationLatitude, request.DestinationLongitude);

            var match = await matchDriver.ExecuteAsync(pickup, [], ct);
            if (match.Candidate is null)
            {
                logger.LogInformation("No driver available for trip {TripId}", request.TripId);
                await PublishFailedAsync(context, "NoDriverAvailable");
                return;
            }

            var quote = await createQuote.ExecuteAsync(pickup, destination, request.Mode, ct, request.EventId);

            await context.Publish(
                new QuoteReadyV1(
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
                    CausationId: request.EventId),
                publish => TraceHeaders.Forward(context, publish),
                ct);
        }
        catch (MatchingDomainException ex)
        {
            // Permanent: the request itself is invalid (bad coordinates, unsupported mode). Do not retry.
            logger.LogWarning(ex, "Quote request for trip {TripId} is invalid", request.TripId);
            await PublishFailedAsync(context, "InvalidRequest");
        }
    }

    private Task PublishFailedAsync(ConsumeContext<QuoteRequestedV1> context, string reasonCode) =>
        context.Publish(
            new QuoteFailedV1(
                EventId: Guid.NewGuid(),
                TripId: context.Message.TripId,
                ReasonCode: reasonCode,
                OccurredAtUtc: clock.GetUtcNow(),
                CorrelationId: context.Message.CorrelationId,
                CausationId: context.Message.EventId),
            publish => TraceHeaders.Forward(context, publish),
            context.CancellationToken);
}
