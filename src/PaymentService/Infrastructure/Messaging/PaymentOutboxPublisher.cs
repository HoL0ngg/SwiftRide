using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Infrastructure.Persistence;

namespace SwiftRide.PaymentService.Infrastructure.Messaging;

public sealed class PaymentOutboxPublisher : BackgroundService
{
    private static readonly ActivitySource OutboxActivitySource = new("SwiftRide.PaymentService.Outbox", "1.0.0");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentOutboxPublisher> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public PaymentOutboxPublisher(IServiceScopeFactory scopeFactory, ILogger<PaymentOutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaymentOutboxPublisher started.");

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
                _logger.LogError(ex, "Unexpected error in PaymentOutboxPublisher loop.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        _logger.LogInformation("PaymentOutboxPublisher stopped.");
    }

    public async Task<bool> ProcessNextBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
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
            return true;
        }

        // 2. Publish message
        var publishSuccess = false;
        string? failureReason = null;

        if (publishEndpoint is null)
        {
            failureReason = "IPublishEndpoint is not registered or broker is unavailable.";
            _logger.LogWarning("Cannot publish payment outbox message {MessageId}: {Reason}", candidate.MessageId, failureReason);
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
                _logger.LogWarning(ex, "Failed to publish payment outbox message {MessageId} of type {EventType}", candidate.MessageId, candidate.EventType);
            }
        }

        // 3. Complete or schedule retry with fencing check
        await using var updateDb = _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<PaymentDbContext>();
        var toUpdate = await updateDb.IntegrationOutbox
            .FirstOrDefaultAsync(o => o.MessageId == candidate.MessageId, ct);

        if (toUpdate is null || toUpdate.Owner != ownerId)
        {
            _logger.LogWarning("Lost lease ownership for payment outbox message {MessageId}. Stale owner {OwnerId} will not commit outcome.", candidate.MessageId, ownerId);
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
                _logger.LogError("Payment outbox message {MessageId} exhausted maximum attempts ({MaxAttempts}). Set RequiresAction=true.", toUpdate.MessageId, toUpdate.MaxAttempts);
            }
            else
            {
                var baseDelay = Math.Min(60.0, Math.Pow(2, Math.Min(10, toUpdate.Attempts - 1)));
                var jitter = baseDelay * (Random.Shared.NextDouble() * 0.25);
                toUpdate.NextAttemptUtc = completionTime.AddSeconds(baseDelay + jitter);
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
            _logger.LogWarning("Concurrency conflict updating outcome for payment message {MessageId}.", candidate.MessageId);
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
            case nameof(PaymentSettledV1):
                var settled = JsonSerializer.Deserialize<PaymentSettledV1>(payloadJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize {nameof(PaymentSettledV1)}");
                if (!string.IsNullOrWhiteSpace(traceParent))
                    await endpoint.Publish(settled, ctx => ctx.Headers.Set("traceparent", traceParent), ct);
                else
                    await endpoint.Publish(settled, ct);
                break;

            case nameof(PaymentFailedV1):
                var failed = JsonSerializer.Deserialize<PaymentFailedV1>(payloadJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize {nameof(PaymentFailedV1)}");
                if (!string.IsNullOrWhiteSpace(traceParent))
                    await endpoint.Publish(failed, ctx => ctx.Headers.Set("traceparent", traceParent), ct);
                else
                    await endpoint.Publish(failed, ct);
                break;

            case nameof(PaymentRefundedV1):
                var refunded = JsonSerializer.Deserialize<PaymentRefundedV1>(payloadJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize {nameof(PaymentRefundedV1)}");
                if (!string.IsNullOrWhiteSpace(traceParent))
                    await endpoint.Publish(refunded, ctx => ctx.Headers.Set("traceparent", traceParent), ct);
                else
                    await endpoint.Publish(refunded, ct);
                break;

            case nameof(PaymentCompensationFailedV1):
                var compFailed = JsonSerializer.Deserialize<PaymentCompensationFailedV1>(payloadJson, JsonOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize {nameof(PaymentCompensationFailedV1)}");
                if (!string.IsNullOrWhiteSpace(traceParent))
                    await endpoint.Publish(compFailed, ctx => ctx.Headers.Set("traceparent", traceParent), ct);
                else
                    await endpoint.Publish(compFailed, ct);
                break;

            default:
                throw new NotSupportedException($"Unsupported payment outbox event type: {eventType}");
        }
    }
}
