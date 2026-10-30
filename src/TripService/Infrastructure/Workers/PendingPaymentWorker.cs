using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Payments;
using SwiftRide.TripService.Application.Trips;

namespace SwiftRide.TripService.Infrastructure.Workers;

public sealed class PendingPaymentWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PendingPaymentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PendingPaymentWorker background service started");

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
                logger.LogError(ex, "Unexpected error in PendingPaymentWorker loop");
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

        logger.LogInformation("PendingPaymentWorker background service stopped");
    }

    private async Task ProcessPendingWorkAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var workStore = scope.ServiceProvider.GetRequiredService<ITripWorkStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<TripPaymentCoordinator>();

        var ownerId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();

        var lease = await workStore.TryClaimAsync(TripWorkKind.Payment, ownerId, now, ct);
        if (lease is null) return;

        logger.LogInformation("Claimed payment lease for trip {TripId} with owner {OwnerId}, attempt {Attempt}",
            lease.TripId, ownerId, lease.AttemptCount);

        await coordinator.ResumeAsync(lease, ct);
    }
}
