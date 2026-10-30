using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Reliability;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Messaging;

public sealed class TripOutboxPublisher : BackgroundService
{
    private static readonly ActivitySource OutboxActivitySource = new("SwiftRide.TripService.Outbox", "1.0.0");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TripOutboxPublisher> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TripOutboxPublisher(IServiceScopeFactory scopeFactory, ILogger<TripOutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TripOutboxPublisher started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextBatchAsync(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in TripOutboxPublisher loop.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        _logger.LogInformation("TripOutboxPublisher stopped.");
    }

    public async Task<bool> ProcessNextBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetService<IPublishEndpoint>();

        var now = DateTimeOffset.UtcNow;
        var ownerId = Guid.NewGuid();

        // 1. Claim pending outbox message atomically
        var candidate = await db.IntegrationOutbox
            .Where(o => o.PublishedAtUtc == null &&
                        !o.RequiresAction &&
                        (o.NextAttemptUtc == null || o.NextAttemptUtc <= now) &&
                        (o.Owner == null || o.LeaseUntilUtc == null || o.LeaseUntilUtc < now))
            .OrderBy(o => o.NextAttemptUtc ?? o.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (candidate is null)
        {
            return false;
        }

        candidate.Owner = ownerId;
        candidate.LeaseUntilUtc = now.Add(LeaseDuration);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker claimed this message, retry on next loop
            return true;
        }

        // 2. Publish message
        var publishSuccess = false;
        string? failureReason = null;

        if (publishEndpoint is null)
        {
            failureReason = "IPublishEndpoint is not registered or broker is unavailable.";
            _logger.LogWarning("Cannot publish outbox message {MessageId}: {Reason}", candidate.MessageId, failureReason);
        }
        else
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PublishTimeout);

            try
            {
                ActivityContext parentContext = default;
                if (!string.IsNullOrWhiteSpace(candidate.TraceParent) &&
                    ActivityContext.TryParse(candidate.TraceParent, null, out var parsed))
                {
                    parentContext = parsed;
                }

                using var activity = OutboxActivitySource.StartActivity(
                    $"Publish {candidate.EventType}",
                    ActivityKind.Producer,
                    parentContext);

                var traceToPropagate = activity?.Id ?? candidate.TraceParent;
                await PublishMessageAsync(publishEndpoint, candidate.EventType, candidate.PayloadJson, traceToPropagate, timeoutCts.Token);

                publishSuccess = true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                _logger.LogWarning(ex, "Failed to publish outbox message {MessageId} of type {EventType}", candidate.MessageId, candidate.EventType);
            }
        }

        // 3. Complete or schedule retry with fencing check
        await using var updateDb = _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<TripDbContext>();
        var toUpdate = await updateDb.IntegrationOutbox
            .FirstOrDefaultAsync(o => o.MessageId == candidate.MessageId, ct);

        if (toUpdate is null || toUpdate.Owner != ownerId)
        {
            _logger.LogWarning("Lost lease ownership for outbox message {MessageId}. Stale owner {OwnerId} will not commit outcome.", candidate.MessageId, ownerId);
            return true;
        }

        var completionTime = DateTimeOffset.UtcNow;
        if (publishSuccess)
        {
            toUpdate.PublishedAtUtc = completionTime;
            toUpdate.Owner = null;
            toUpdate.LeaseUntilUtc = null;
            toUpdate.LastError = null;
        }
        else
        {
            toUpdate.Attempts += 1;
            toUpdate.LastError = failureReason?.Length > 4000 ? failureReason[..4000] : failureReason;

            if (toUpdate.Attempts >= toUpdate.MaxAttempts)
            {
                toUpdate.RequiresAction = true;
                toUpdate.Owner = null;
                toUpdate.LeaseUntilUtc = null;
                _logger.LogError("Outbox message {MessageId} exhausted maximum attempts ({MaxAttempts}). Set RequiresAction=true.", toUpdate.MessageId, toUpdate.MaxAttempts);
            }
            else
            {
                var delay = RetrySchedule.NextDelay(toUpdate.Attempts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), Random.Shared.NextDouble());
                toUpdate.NextAttemptUtc = completionTime.Add(delay);
                toUpdate.Owner = null;
                toUpdate.LeaseUntilUtc = null;
            }
        }

        try
        {
            await updateDb.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("Concurrency conflict updating outcome for message {MessageId}.", candidate.MessageId);
        }

        return true;
    }

    private static async Task PublishMessageAsync(
        IPublishEndpoint endpoint,
        string eventType,
        string payloadJson,
        string? traceParent,
        CancellationToken ct)
    {
        switch (eventType)
        {
            case nameof(TripAcceptedV1):
                var accepted = JsonSerializer.Deserialize<TripAcceptedV1>(payloadJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize {nameof(TripAcceptedV1)}");
                if (!string.IsNullOrWhiteSpace(traceParent))
                    await endpoint.Publish(accepted, ctx => ctx.Headers.Set("traceparent", traceParent), ct);
                else
                    await endpoint.Publish(accepted, ct);
                break;

            case nameof(RideCompletedV1):
                var completed = JsonSerializer.Deserialize<RideCompletedV1>(payloadJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize {nameof(RideCompletedV1)}");
                if (!string.IsNullOrWhiteSpace(traceParent))
                    await endpoint.Publish(completed, ctx => ctx.Headers.Set("traceparent", traceParent), ct);
                else
                    await endpoint.Publish(completed, ct);
                break;

            case nameof(PaymentCompensationRequestedV1):
                var comp = JsonSerializer.Deserialize<PaymentCompensationRequestedV1>(payloadJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize {nameof(PaymentCompensationRequestedV1)}");
                if (!string.IsNullOrWhiteSpace(traceParent))
                    await endpoint.Publish(comp, ctx => ctx.Headers.Set("traceparent", traceParent), ct);
                else
                    await endpoint.Publish(comp, ct);
                break;

            default:
                throw new NotSupportedException($"Unsupported outbox event type: {eventType}");
        }
    }
}
