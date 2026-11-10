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

            await context.Publish(
                new DriverMatchedV1(
                    EventId: Guid.NewGuid(),
                    TripId: request.TripId,
                    DriverId: match.Candidate.DriverId,
                    DistanceKm: match.Candidate.DistanceKm,
                    ExcludedDriverIds: excluded,
                    OccurredAtUtc: clock.GetUtcNow(),
                    CorrelationId: request.CorrelationId,
                    CausationId: request.EventId),
                publish => TraceHeaders.Forward(context, publish),
                ct);
        }
        catch (MatchingDomainException ex)
        {
            logger.LogWarning(ex, "Driver match request for trip {TripId} is invalid", request.TripId);
            await PublishFailedAsync(context, "InvalidRequest");
        }
    }

    private Task PublishFailedAsync(ConsumeContext<DriverMatchRequestedV1> context, string reasonCode) =>
        context.Publish(
            new DriverMatchFailedV1(
                EventId: Guid.NewGuid(),
                TripId: context.Message.TripId,
                ReasonCode: reasonCode,
                OccurredAtUtc: clock.GetUtcNow(),
                CorrelationId: context.Message.CorrelationId,
                CausationId: context.Message.EventId),
            publish => TraceHeaders.Forward(context, publish),
            context.CancellationToken);
}
