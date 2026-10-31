using Microsoft.Extensions.Logging;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Reliability;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Application.Trips;

public sealed class TripWorkCoordinator(
    ITripReader reader,
    ITripWriter writer,
    ITripUnitOfWork unitOfWork,
    ICommandReceiptStore receiptStore,
    IActiveAssignmentStore assignmentStore,
    IMatchingClient matchingClient,
    TimeProvider clock,
    ILogger<TripWorkCoordinator> logger)
{
    public async Task<bool> ExecuteInitialMatchingAsync(TripContext context, CommandReceipt receipt, CancellationToken ct)
    {
        try
        {
            await PerformMatchingAndQuoteAsync(context, ct);

            receipt.Status = "completed";
            receipt.ResponseJson = context.Trip.Id.ToString("D");
            receipt.UpdatedAt = clock.GetUtcNow();

            writer.Update(context);
            receiptStore.Update(receipt);
            await unitOfWork.SaveAsync(ct);
            return true;
        }
        catch (ServiceClientAuthException ex)
        {
            logger.LogError(ex, "Initial matching for trip {TripId} encountered auth error; marking receipt failed", context.Trip.Id);
            receipt.Status = "failed";
            receipt.UpdatedAt = clock.GetUtcNow();
            receiptStore.Update(receipt);
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (ServiceClientConflictException ex)
        {
            logger.LogError(ex, "Initial matching for trip {TripId} encountered conflict error; marking receipt failed", context.Trip.Id);
            receipt.Status = "failed";
            receipt.UpdatedAt = clock.GetUtcNow();
            receiptStore.Update(receipt);
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ServiceClientTransientException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Initial matching for trip {TripId} encountered transient error; scheduling retry", context.Trip.Id);
            receipt.Attempts++;
            var nextDelay = RetrySchedule.NextDelay(receipt.Attempts, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
            receipt.NextAttemptUtc = clock.GetUtcNow().Add(nextDelay);
            receipt.UpdatedAt = clock.GetUtcNow();

            receiptStore.Update(receipt);
            await unitOfWork.SaveAsync(ct);
            return false;
        }
        catch (ServiceClientException ex)
        {
            logger.LogError(ex, "Initial matching for trip {TripId} encountered permanent client error; marking receipt failed", context.Trip.Id);
            receipt.Status = "failed";
            receipt.UpdatedAt = clock.GetUtcNow();
            receiptStore.Update(receipt);
            await unitOfWork.SaveAsync(ct);
            return false;
        }
    }

    public async Task<bool> ResumeMatchingAsync(TripWorkLease lease, CancellationToken ct)
    {
        var context = await reader.LoadAsync(lease.TripId, ct);
        if (context is null || context.Trip.Status != TripStatus.Requested)
        {
            logger.LogInformation("Trip {TripId} is no longer in Requested state (Status: {Status}); skipping matching",
                lease.TripId, context?.Trip.Status);
            return true;
        }

        await PerformMatchingAndQuoteAsync(context, ct);

        writer.Update(context);
        await unitOfWork.SaveAsync(ct);
        return true;
    }

    private async Task<bool> PerformMatchingAndQuoteAsync(TripContext context, CancellationToken ct)
    {
        var pickup = new GeoCoordinate(context.PickupLat, context.PickupLon);
        var dest = new GeoCoordinate(context.DestLat, context.DestLon);

        var match = await matchingClient.MatchAsync(new MatchInput(pickup, Array.Empty<string>()), ct);
        if (match.Status == MatchStatus.NoDriverAvailable || match.Candidate is null)
        {
            context.Trip.FailNoDriverAvailable();
            assignmentStore.ReleaseRider(context.Trip.Id);
            context.UpdatedAt = clock.GetUtcNow();
            logger.LogInformation("Trip {TripId} failed matching: NoDriverAvailable", context.Trip.Id);
            return true;
        }

        var quote = await matchingClient.QuoteAsync(new QuoteInput(pickup, dest, context.PricingMode), ct);
        context.Trip.ApplyQuote(new FareSnapshot(quote.QuoteId, quote.Amount, quote.Currency), match.Candidate.DriverId);
        context.DistanceKm = quote.DistanceKm;
        context.EstimatedMinutes = quote.EstimatedMinutes;
        context.UpdatedAt = clock.GetUtcNow();

        logger.LogInformation("Trip {TripId} quoted with driver {DriverId}, fare {Fare} {Currency}",
            context.Trip.Id, match.Candidate.DriverId, quote.Amount, quote.Currency);
        return true;
    }

    public async Task FailMatchingExhaustedAsync(TripWorkLease lease, CancellationToken ct)
    {
        var context = await reader.LoadAsync(lease.TripId, ct);
        if (context is not null && context.Trip.Status == TripStatus.Requested)
        {
            context.Trip.FailNoDriverAvailable();
            assignmentStore.ReleaseRider(context.Trip.Id);
            context.UpdatedAt = clock.GetUtcNow();

            writer.Update(context);
            logger.LogInformation("Trip {TripId} matching retry limit exceeded; marked Failed and released rider {RiderId}",
                context.Trip.Id, context.Trip.RiderId);
        }
    }
}
