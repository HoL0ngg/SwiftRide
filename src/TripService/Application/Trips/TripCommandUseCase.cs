using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Messaging;
using SwiftRide.TripService.Application.Payments;
using SwiftRide.TripService.Domain;

namespace SwiftRide.TripService.Application.Trips;

public sealed class TripCommandUseCase(
    ITripReader reader,
    ITripWriter writer,
    ITripUnitOfWork unitOfWork,
    ICommandReceiptStore receiptStore,
    IActiveAssignmentStore assignmentStore,
    ITripWorkStore workStore,
    IMatchingClient matchingClient,
    TimeProvider clock,
    ILogger<TripCommandUseCase> logger,
    TripPaymentCoordinator? paymentCoordinator = null,
    IOptions<PaymentWorkflowOptions>? paymentOptions = null,
    ITripEventWriter? eventWriter = null)
{
    private readonly PaymentWorkflowOptions _paymentOptions = paymentOptions?.Value ?? new PaymentWorkflowOptions();
    public async Task<TripCommandOutcome> ExecuteAsync(
        TripActor actor,
        Guid tripId,
        TripCommand command,
        string commandKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandKey);

        var payloadHash = ComputePayloadHash(command);
        var scope = new CommandScope(actor.Id, actor.Role, command.ActionName, tripId);

        var existingReceipt = await receiptStore.FindAsync(scope, commandKey, ct);
        if (existingReceipt is not null)
        {
            if (existingReceipt.RequestHash != payloadHash)
            {
                throw new IdempotencyConflictException(
                    $"Command key '{commandKey}' was already used with a different payload for action '{command.ActionName}'.");
            }

            var existingCtx = await reader.LoadAsync(tripId, ct);
            if (existingCtx is null)
            {
                throw new TripNotFoundException(tripId);
            }

            logger.LogInformation("Replaying trip command {Action} for trip {TripId} with key {Key}",
                command.ActionName, tripId, commandKey);

            return new TripCommandOutcome(TripMapper.ToReadModel(existingCtx), IsReplay: true, command.ActionName);
        }

        var ctx = await reader.LoadAsync(tripId, ct);
        if (ctx is null)
        {
            throw new TripNotFoundException(tripId);
        }

        var trip = ctx.Trip;
        var now = clock.GetUtcNow();

        switch (command)
        {
            case ConfirmQuoteCommand confirm:
                if (actor.Role != "rider" || actor.Id != trip.RiderId)
                {
                    throw new UnauthorizedTripAccessException("Only the trip rider can confirm quote.");
                }

                trip.ConfirmQuote(actor.Id, confirm.QuoteId);
                return await MatchAndOfferDriverAsync(ctx, scope, commandKey, payloadHash, command.ActionName, now, ct);

            case DriverAcceptCommand:
                if (actor.Role != "driver")
                {
                    throw new UnauthorizedTripAccessException("Only a driver can accept a trip offer.");
                }
                if (trip.OfferedDriverId is null || trip.OfferedDriverId != actor.Id)
                {
                    throw new UnauthorizedTripAccessException("Driver is not the currently offered driver for this trip.");
                }

                trip.AcceptDriver(actor.Id);
                eventWriter?.Add(new TripAcceptedV1(
                    EventId: Guid.NewGuid(),
                    TripId: trip.Id,
                    RiderId: trip.RiderId,
                    DriverId: actor.Id,
                    OccurredAtUtc: now,
                    CorrelationId: $"trip-{trip.Id:D}-accept"));
                logger.LogInformation("Trip {TripId} accepted by driver {DriverId}", trip.Id, actor.Id);
                break;

            case DriverRejectCommand:
                if (actor.Role != "driver")
                {
                    throw new UnauthorizedTripAccessException("Only a driver can reject a trip offer.");
                }
                if (trip.OfferedDriverId is null || trip.OfferedDriverId != actor.Id)
                {
                    throw new UnauthorizedTripAccessException("Driver is not the currently offered driver for this trip.");
                }

                trip.RejectDriver(actor.Id);
                assignmentStore.ReleaseDriver(trip.Id);

                if (trip.Status == TripStatus.Failed)
                {
                    assignmentStore.ReleaseRider(trip.Id);
                    ctx.UpdatedAt = now;
                    writer.Update(ctx);
                    receiptStore.Add(CreateReceipt(scope, commandKey, payloadHash, trip.Id, now));
                    await unitOfWork.SaveAsync(ct);
                    logger.LogInformation("Trip {TripId} reached max rejections ({Count}) and failed", trip.Id, trip.RejectionCount);
                    return new TripCommandOutcome(TripMapper.ToReadModel(ctx), IsReplay: false, command.ActionName);
                }

                if (trip.Status == TripStatus.DriverOffered)
                {
                    return await MatchAndOfferDriverAsync(ctx, scope, commandKey, payloadHash, command.ActionName, now, ct);
                }
                break;

            case EnRouteCommand:
                if (actor.Role != "driver" || trip.AssignedDriverId != actor.Id)
                {
                    throw new UnauthorizedTripAccessException("Only the assigned driver can start en-route.");
                }

                trip.StartEnRoute(actor.Id);
                logger.LogInformation("Trip {TripId} driver {DriverId} started en-route", trip.Id, actor.Id);
                break;

            case PickupCommand:
                if (actor.Role != "driver" || trip.AssignedDriverId != actor.Id)
                {
                    throw new UnauthorizedTripAccessException("Only the assigned driver can pick up the rider.");
                }

                trip.Pickup(actor.Id);
                logger.LogInformation("Trip {TripId} rider picked up by driver {DriverId}", trip.Id, actor.Id);
                break;

            case DropoffCommand:
                if (actor.Role != "driver" || trip.AssignedDriverId != actor.Id)
                {
                    throw new UnauthorizedTripAccessException("Only the assigned driver can drop off the rider.");
                }

                trip.Dropoff(actor.Id, now);
                trip.RequestPayment();
                assignmentStore.ReleaseDriver(trip.Id);

                var paymentKey = trip.PaymentIdempotencyKey ?? $"trip-{trip.Id:D}-payment";
                var maxAttempts = _paymentOptions.MaxAttempts;
                var deadlineUtc = now.Add(_paymentOptions.TotalBudget);
                var transport = ctx.PaymentTransport;
                if (transport.Equals("Events", StringComparison.OrdinalIgnoreCase) &&
                    (eventWriter is null || !eventWriter.IsAvailable))
                {
                    logger.LogWarning("Trip {TripId} was configured for Events transport but event writer is unavailable. Falling back to Rest transport to prevent stuck payment.", trip.Id);
                    transport = "Rest";
                    ctx.PaymentTransport = "Rest";
                }

                workStore.AddPaymentJob(trip.Id, paymentKey, now, maxAttempts, deadlineUtc, transport);

                if (transport.Equals("Events", StringComparison.OrdinalIgnoreCase))
                {
                    eventWriter?.Add(new RideCompletedV1(
                        EventId: Guid.NewGuid(),
                        TripId: trip.Id,
                        RiderId: trip.RiderId,
                        DriverId: actor.Id,
                        QuoteId: trip.Fare?.QuoteId ?? Guid.Empty,
                        FareAmount: trip.Fare?.Amount ?? 0m,
                        FareCurrency: trip.Fare?.Currency ?? "VND",
                        PaymentMethod: ctx.PaymentMethod,
                        MockTokenAlias: ctx.MockTokenAlias,
                        PaymentIdempotencyKey: paymentKey,
                        OccurredAtUtc: now,
                        CorrelationId: $"trip-{trip.Id:D}-dropoff"));
                }

                logger.LogInformation("Trip {TripId} dropped off by driver {DriverId}, payment job scheduled with key {PaymentKey} (Transport: {Transport})",
                    trip.Id, actor.Id, paymentKey, transport);
                break;

            case CancelTripCommand cancel:
                if (actor.Role != "rider" || trip.RiderId != actor.Id)
                {
                    throw new UnauthorizedTripAccessException("Only the trip rider can cancel.");
                }

                var hadOfferedOrAssignedDriver = trip.OfferedDriverId is not null || trip.AssignedDriverId is not null;
                trip.Cancel(actor.Id);
                assignmentStore.ReleaseRider(trip.Id);

                if (hadOfferedOrAssignedDriver)
                {
                    assignmentStore.ReleaseDriver(trip.Id);
                }

                logger.LogInformation("Trip {TripId} cancelled by rider {RiderId}. Reason: {Reason}",
                    trip.Id, actor.Id, cancel.Reason ?? "unspecified");
                break;

            default:
                throw new InvalidTripCommandException($"Unknown command type: {command.GetType().Name}");
        }

        ctx.UpdatedAt = now;
        writer.Update(ctx);
        receiptStore.Add(CreateReceipt(scope, commandKey, payloadHash, trip.Id, now));
        await unitOfWork.SaveAsync(ct);

        if (command is DropoffCommand && paymentCoordinator is not null && !ctx.PaymentTransport.Equals("Events", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await paymentCoordinator.ExecuteInitialPaymentAsync(ctx, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Initial automatic payment attempt for trip {TripId} encountered an error; background worker will reconcile", ctx.Trip.Id);
            }
        }

        return new TripCommandOutcome(TripMapper.ToReadModel(ctx), IsReplay: false, command.ActionName);
    }

    private async Task<TripCommandOutcome> MatchAndOfferDriverAsync(
        TripContext ctx,
        CommandScope scope,
        string commandKey,
        string payloadHash,
        string actionName,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var trip = ctx.Trip;
        var pickup = new GeoCoordinate(ctx.PickupLat, ctx.PickupLon);
        var excludedDrivers = new HashSet<string>(trip.RejectedDriverIds);
        var claimedDriver = false;
        var attempts = 0;

        while (!claimedDriver && trip.RejectionCount <= trip.MaxDriverRejections && attempts <= trip.MaxDriverRejections)
        {
            attempts++;
            var matchResult = await matchingClient.MatchAsync(
                new MatchInput(pickup, excludedDrivers.Take(100).ToArray()), ct);

            if (matchResult.Status != MatchStatus.Matched || matchResult.Candidate is null)
            {
                break;
            }

            var candidateId = matchResult.Candidate.DriverId;
            if (!assignmentStore.TryClaimDriver(candidateId, trip.Id))
            {
                logger.LogInformation("Trip {TripId} driver {DriverId} is busy; finding another", trip.Id, candidateId);
                excludedDrivers.Add(candidateId);
                continue;
            }

            trip.OfferReplacementDriver(candidateId);
            ctx.UpdatedAt = now;
            writer.Update(ctx);
            receiptStore.Add(CreateReceipt(scope, commandKey, payloadHash, trip.Id, now));

            try
            {
                await unitOfWork.SaveAsync(ct);
                claimedDriver = true;
                logger.LogInformation("Trip {TripId} offered to driver {DriverId} and committed atomically", trip.Id, candidateId);
                return new TripCommandOutcome(TripMapper.ToReadModel(ctx), IsReplay: false, actionName);
            }
            catch (Exception ex) when (IsActiveDriverAssignmentConflict(ex))
            {
                logger.LogWarning(ex, "Trip {TripId} driver {DriverId} conflict on commit; rolling back and rematching", trip.Id, candidateId);
                assignmentStore.ReleaseDriver(trip.Id);
                excludedDrivers.Add(candidateId);
                trip.WithdrawOffer();
            }
        }

        trip.FailNoDriverAvailable();
        assignmentStore.ReleaseRider(trip.Id);
        ctx.UpdatedAt = now;
        writer.Update(ctx);
        receiptStore.Add(CreateReceipt(scope, commandKey, payloadHash, trip.Id, now));
        await unitOfWork.SaveAsync(ct);
        logger.LogInformation("Trip {TripId}: No available driver, trip failed", trip.Id);
        return new TripCommandOutcome(TripMapper.ToReadModel(ctx), IsReplay: false, actionName);
    }

    private static CommandReceipt CreateReceipt(CommandScope scope, string commandKey, string payloadHash, Guid tripId, DateTimeOffset now) =>
        new(scope.ToScopeString(), commandKey, payloadHash, tripId.ToString("D"), "completed", 1, null, now, now);

    private static string ComputePayloadHash(TripCommand command)
    {
        var rawPayload = command switch
        {
            ConfirmQuoteCommand c => c.QuoteId.ToString("D"),
            CancelTripCommand c => c.Reason ?? string.Empty,
            _ => string.Empty
        };

        var bytes = Encoding.UTF8.GetBytes(rawPayload);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool IsActiveDriverAssignmentConflict(Exception ex)
    {
        if (!ex.GetType().Name.Equals("DbUpdateException", StringComparison.Ordinal))
        {
            return false;
        }

        // Must involve ActiveDriverAssignment entity or table
        var text = ex.ToString();
        var involvesAssignment = text.Contains("ActiveDriverAssignment", StringComparison.OrdinalIgnoreCase);
        if (!involvesAssignment)
        {
            return false;
        }

        // Must be a unique constraint / duplicate key violation (SQL Server error 2601 / 2627 or duplicate key indicator)
        var isUniqueViolation = text.Contains("2601", StringComparison.Ordinal)
            || text.Contains("2627", StringComparison.Ordinal)
            || text.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unique", StringComparison.OrdinalIgnoreCase);

        return isUniqueViolation;
    }
}
