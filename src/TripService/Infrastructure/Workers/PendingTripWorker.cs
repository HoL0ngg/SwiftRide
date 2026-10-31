using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;

namespace SwiftRide.TripService.Infrastructure.Workers;

public sealed class PendingTripWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PendingTripWorker> logger,
    IOptions<TripRecoveryOptions>? recoveryOptions = null) : BackgroundService
{
    private readonly TripRecoveryOptions _recoveryOptions = recoveryOptions?.Value ?? new TripRecoveryOptions();
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PendingTripWorker background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in PendingTripWorker loop");
            }

            try
            {
                await Task.Delay(Interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("PendingTripWorker background service stopped");
    }

    private async Task ProcessPendingWorkAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var workStore = scope.ServiceProvider.GetRequiredService<ITripWorkStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<TripWorkCoordinator>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ITripUnitOfWork>();

        var ownerId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();

        var lease = await workStore.TryClaimAsync(TripWorkKind.Matching, ownerId, now, ct);
        if (lease is null) return;

        logger.LogInformation("Claimed matching lease for trip {TripId}, attempt {Attempt}", lease.TripId, lease.AttemptCount);

        try
        {
            var success = await coordinator.ResumeMatchingAsync(lease, ct);
            if (success)
            {
                workStore.StageCompletion(lease);
                await unitOfWork.SaveAsync(ct);
                logger.LogInformation("Successfully completed matching for trip {TripId}", lease.TripId);
            }
            else
            {
                if (lease.AttemptCount >= _recoveryOptions.MatchingMaxAttempts)
                {
                    await coordinator.FailMatchingExhaustedAsync(lease, ct);
                    workStore.StageRequiresAction(lease, "matching_attempts_exhausted");
                    await unitOfWork.SaveAsync(ct);
                    logger.LogInformation("Matching attempts exhausted for trip {TripId}; marked Failed and released rider", lease.TripId);
                }
                else
                {
                    var nextDelay = SwiftRide.TripService.Application.Reliability.RetrySchedule.NextDelay(
                        lease.AttemptCount, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
                    var nextAttempt = now.Add(nextDelay);
                    workStore.StageRetry(lease, nextAttempt, "transient_matching_error");
                    await unitOfWork.SaveAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resume matching for trip {TripId}", lease.TripId);
            if (lease.AttemptCount >= _recoveryOptions.MatchingMaxAttempts)
            {
                await coordinator.FailMatchingExhaustedAsync(lease, ct);
                workStore.StageRequiresAction(lease, "matching_exception_exhausted");
                await unitOfWork.SaveAsync(ct);
            }
            else
            {
                var nextDelay = SwiftRide.TripService.Application.Reliability.RetrySchedule.NextDelay(
                    lease.AttemptCount, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
                var nextAttempt = now.Add(nextDelay);
                workStore.StageRetry(lease, nextAttempt, "exception_resuming");
                await unitOfWork.SaveAsync(ct);
            }
        }
    }
}
