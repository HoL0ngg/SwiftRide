using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Reliability;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Application.Payments;

public sealed class TripPaymentCoordinator(
    ITripReader reader,
    ITripWriter writer,
    ITripUnitOfWork unitOfWork,
    IActiveAssignmentStore assignmentStore,
    ITripWorkStore workStore,
    IPaymentClient paymentClient,
    IOptions<PaymentWorkflowOptions> options,
    TimeProvider clock,
    ILogger<TripPaymentCoordinator> logger,
    ITripMetrics? metrics = null)
{
    private readonly PaymentWorkflowOptions _options = options.Value;
    private readonly ITripMetrics _metrics = metrics ?? NullTripMetrics.Instance;

    public async Task<bool> ExecuteInitialPaymentAsync(TripContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Trip.Status != TripStatus.PaymentPending)
        {
            logger.LogInformation("Trip {TripId} is not in PaymentPending state (current: {Status}); skipping initial charge",
                ctx.Trip.Id, ctx.Trip.Status);
            return false;
        }

        var idempotencyKey = ctx.Trip.PaymentIdempotencyKey ?? $"trip-{ctx.Trip.Id:D}-payment";
        var chargeReq = new ChargeRequest(
            ctx.Trip.Id,
            ctx.Trip.RiderId,
            ctx.Trip.AssignedDriverId,
            ctx.Trip.Fare!.Amount,
            ctx.Trip.Fare.Currency,
            ctx.PaymentMethod,
            ctx.MockTokenAlias,
            idempotencyKey);

        try
        {
            var observation = await paymentClient.ChargeAsync(chargeReq, ct);
            return await HandlePaymentObservationAsync(ctx, observation, lease: null, ct);
        }
        catch (ServiceClientAuthException ex)
        {
            logger.LogError(ex, "Initial charge for trip {TripId} encountered auth error; staging RequiresAction", ctx.Trip.Id);
            workStore.StageUnownedRequiresAction(ctx.Trip.Id, "auth_failure");
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (ServiceClientConflictException ex)
        {
            logger.LogError(ex, "Initial charge for trip {TripId} encountered conflict error; staging RequiresAction", ctx.Trip.Id);
            workStore.StageUnownedRequiresAction(ctx.Trip.Id, "conflict_failure");
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ServiceClientTransientException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Initial charge for trip {TripId} encountered transport failure; scheduling reconciliation", ctx.Trip.Id);

            workStore.StageUnownedRetry(ctx.Trip.Id, clock.GetUtcNow().Add(_options.InitialBackoff), "transient_error");
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (ServiceClientException ex)
        {
            logger.LogError(ex, "Initial charge for trip {TripId} encountered permanent client error; staging RequiresAction", ctx.Trip.Id);
            workStore.StageUnownedRequiresAction(ctx.Trip.Id, "permanent_client_error");
            await unitOfWork.SaveAsync(ct);
            return false;
        }
    }

    public async Task<bool> ResumeAsync(TripWorkLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var maxAttempts = lease.MaxAttempts > 0 ? lease.MaxAttempts : _options.MaxAttempts;

        if (lease.DeadlineUtc.HasValue && clock.GetUtcNow() >= lease.DeadlineUtc.Value)
        {
            logger.LogWarning("Trip {TripId} payment deadline ({Deadline}) expired; staging RequiresAction without network calls",
                lease.TripId, lease.DeadlineUtc.Value);
            workStore.StageRequiresAction(lease, "deadline_expired");
            await unitOfWork.SaveAsync(ct);
            return false;
        }

        if (lease.AttemptCount > maxAttempts)
        {
            logger.LogWarning("Trip {TripId} payment reconciliation exceeded max attempts ({Attempts} > {MaxAttempts}); staging RequiresAction",
                lease.TripId, lease.AttemptCount, maxAttempts);
            workStore.StageRequiresAction(lease, "max_attempts_exceeded");
            await unitOfWork.SaveAsync(ct);
            return false;
        }

        var ctx = await reader.LoadAsync(lease.TripId, ct);
        if (ctx is null || ctx.Trip.Status != TripStatus.PaymentPending)
        {
            logger.LogInformation("Trip {TripId} is no longer in PaymentPending state; completing payment job lease", lease.TripId);
            workStore.StageCompletion(lease);
            await unitOfWork.SaveAsync(ct);
            return false;
        }

        try
        {
            var observation = await paymentClient.GetByTripAsync(lease.TripId, ct);
            if (observation is not null)
            {
                if (observation.Status == RemotePaymentStatus.Pending)
                {
                    // Check if payment is ready to retry calling charge
                    var canRetryCharge = false;
                    if (observation.Processing is not null)
                    {
                        if (observation.Processing.RequiresAction)
                        {
                            workStore.StageRequiresAction(lease, "payment_requires_action");
                            await unitOfWork.SaveAsync(ct);
                            return false;
                        }

                        if (observation.Processing.CanRetry &&
                            (observation.Processing.LeaseUntilUtc == null || observation.Processing.LeaseUntilUtc < clock.GetUtcNow()))
                        {
                            canRetryCharge = true;
                        }
                    }
                    else
                    {
                        // Without metadata, if enough time elapsed since trip update, retry charge
                        if (clock.GetUtcNow() - ctx.UpdatedAt >= TimeSpan.FromSeconds(30))
                        {
                            canRetryCharge = true;
                        }
                    }

                    if (canRetryCharge)
                    {
                        logger.LogInformation("Payment for trip {TripId} is Pending and lease expired/can retry; re-invoking charge", lease.TripId);
                        var retryKey = ctx.Trip.PaymentIdempotencyKey ?? $"trip-{ctx.Trip.Id:D}-payment";
                        var retryChargeReq = new ChargeRequest(
                            ctx.Trip.Id,
                            ctx.Trip.RiderId,
                            ctx.Trip.AssignedDriverId,
                            ctx.Trip.Fare!.Amount,
                            ctx.Trip.Fare.Currency,
                            ctx.PaymentMethod,
                            ctx.MockTokenAlias,
                            retryKey);

                        var retryObs = await paymentClient.ChargeAsync(retryChargeReq, ct);
                        return await HandlePaymentObservationAsync(ctx, retryObs, lease, ct);
                    }
                }

                return await HandlePaymentObservationAsync(ctx, observation, lease, ct);
            }

            // Not found on remote payment service: charge with original locked parameters
            var idempotencyKey = ctx.Trip.PaymentIdempotencyKey ?? $"trip-{ctx.Trip.Id:D}-payment";
            var chargeReq = new ChargeRequest(
                ctx.Trip.Id,
                ctx.Trip.RiderId,
                ctx.Trip.AssignedDriverId,
                ctx.Trip.Fare!.Amount,
                ctx.Trip.Fare.Currency,
                ctx.PaymentMethod,
                ctx.MockTokenAlias,
                idempotencyKey);

            var chargeObs = await paymentClient.ChargeAsync(chargeReq, ct);
            return await HandlePaymentObservationAsync(ctx, chargeObs, lease, ct);
        }
        catch (ServiceClientAuthException ex)
        {
            logger.LogError(ex, "Payment reconciliation for trip {TripId} encountered auth error; staging RequiresAction", lease.TripId);
            workStore.StageRequiresAction(lease, "auth_failure");
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (ServiceClientConflictException ex)
        {
            logger.LogError(ex, "Payment reconciliation for trip {TripId} encountered conflict error; staging RequiresAction", lease.TripId);
            workStore.StageRequiresAction(lease, "conflict_failure");
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ServiceClientTransientException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Payment reconciliation for trip {TripId} encountered error; scheduling next attempt", lease.TripId);

            if (lease.AttemptCount >= maxAttempts)
            {
                logger.LogWarning("Trip {TripId} payment reached maximum attempts ({Attempts}/{MaxAttempts}); staging RequiresAction",
                    lease.TripId, lease.AttemptCount, maxAttempts);
                workStore.StageRequiresAction(lease, "max_attempts_exhausted");
                await unitOfWork.SaveAsync(ct);
                return false;
            }

            var nextDelay = RetrySchedule.NextDelay(
                lease.AttemptCount,
                _options.InitialBackoff,
                _options.MaxBackoff,
                jitterFraction: Random.Shared.NextDouble(),
                retryAfter: null,
                deadlineUtc: lease.DeadlineUtc,
                nowUtc: clock.GetUtcNow());

            if (nextDelay <= TimeSpan.Zero || (lease.DeadlineUtc.HasValue && clock.GetUtcNow() + nextDelay > lease.DeadlineUtc.Value))
            {
                logger.LogWarning("Trip {TripId} next retry would exceed deadline; staging RequiresAction", lease.TripId);
                workStore.StageRequiresAction(lease, "deadline_exhausted");
                await unitOfWork.SaveAsync(ct);
                return false;
            }

            workStore.StageRetry(lease, clock.GetUtcNow().Add(nextDelay), "reconciliation_error");
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (ServiceClientException ex)
        {
            logger.LogError(ex, "Payment reconciliation for trip {TripId} encountered permanent client error; staging RequiresAction", lease.TripId);
            workStore.StageRequiresAction(lease, "permanent_client_error");
            await unitOfWork.SaveAsync(ct);
            return false;
        }
    }

    private async Task<bool> HandlePaymentObservationAsync(
        TripContext ctx,
        PaymentObservation obs,
        TripWorkLease? lease,
        CancellationToken ct)
    {
        if (obs.Status == RemotePaymentStatus.Settled)
        {
            // Verify trip identity, locked fare, and ensure payment was not refunded
            if (obs.TripId != ctx.Trip.Id ||
                obs.RiderId != ctx.Trip.RiderId ||
                string.IsNullOrWhiteSpace(obs.DriverId) ||
                obs.DriverId != ctx.Trip.AssignedDriverId ||
                obs.Amount != ctx.Trip.Fare!.Amount ||
                obs.Currency != ctx.Trip.Fare.Currency ||
                obs.HasSettledRefund)
            {
                logger.LogWarning("Settled payment does not match trip identity (TripId: {ObsTripId} vs {TripId}, Rider: {ObsRider} vs {Rider}, Driver: {ObsDriver} vs {Driver}) or locked fare ({Fare} {Currency}) or has refund; cannot transition to Paid",
                    obs.TripId, ctx.Trip.Id, obs.RiderId, ctx.Trip.RiderId, obs.DriverId, ctx.Trip.AssignedDriverId, ctx.Trip.Fare?.Amount, ctx.Trip.Fare?.Currency);
                return false;
            }

            var idempotencyKey = ctx.Trip.PaymentIdempotencyKey ?? $"trip-{ctx.Trip.Id:D}-payment";
            var settlement = new PaymentSettlement(
                obs.Id,
                ctx.Trip.Id,
                obs.Amount,
                obs.Currency,
                idempotencyKey);

            ctx.Trip.SettlePayment(settlement);
            ctx.UpdatedAt = clock.GetUtcNow();
            _metrics.TripCompleted();

            assignmentStore.ReleaseRider(ctx.Trip.Id);
            if (lease is not null)
            {
                workStore.StageCompletion(lease);
            }
            else
            {
                workStore.StageUnownedCompletion(ctx.Trip.Id);
            }

            writer.Update(ctx);
            await unitOfWork.SaveAsync(ct);

            logger.LogInformation("Trip {TripId} payment settled successfully (PaymentId: {PaymentId}); transitioned to Paid",
                ctx.Trip.Id, obs.Id);
            return true;
        }

        if (obs.Status == RemotePaymentStatus.Pending)
        {
            var backoffSeconds = Math.Min(_options.MaxBackoff.TotalSeconds, Math.Pow(2, lease?.AttemptCount ?? 1));
            var nextAttempt = clock.GetUtcNow().AddSeconds(backoffSeconds);

            if (lease is not null)
            {
                workStore.StageRetry(lease, nextAttempt, "payment_pending");
            }
            else
            {
                workStore.StageUnownedRetry(ctx.Trip.Id, nextAttempt, "payment_pending");
            }

            await unitOfWork.SaveAsync(ct);

            logger.LogInformation("Trip {TripId} payment is still Pending on remote; scheduled retry in {Seconds}s",
                ctx.Trip.Id, backoffSeconds);
            return false;
        }

        if (obs.Status == RemotePaymentStatus.Failed)
        {
            ctx.Trip.RecordPaymentFailure(PaymentFailureKind.Permanent);
            ctx.UpdatedAt = clock.GetUtcNow();

            if (lease is not null)
            {
                workStore.StageRequiresAction(lease, "payment_failed");
            }
            else
            {
                workStore.StageUnownedRequiresAction(ctx.Trip.Id, "payment_failed");
            }

            writer.Update(ctx);
            await unitOfWork.SaveAsync(ct);

            logger.LogWarning("Trip {TripId} payment failed on remote; recorded failure", ctx.Trip.Id);
            return false;
        }

        return false;
    }
}
