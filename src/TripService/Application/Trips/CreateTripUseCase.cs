using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Domain;

namespace SwiftRide.TripService.Application.Trips;

public sealed class CreateTripUseCase(
    ITripReader reader,
    ITripWriter writer,
    ITripUnitOfWork unitOfWork,
    ICommandReceiptStore receiptStore,
    IActiveAssignmentStore assignmentStore,
    ITripQueryReader queryReader,
    TripWorkCoordinator coordinator,
    TimeProvider clock,
    IOptions<TripLifecycleOptions>? lifecycleOptions = null,
    SwiftRide.TripService.Application.Messaging.ITripEventWriter? eventWriter = null)
{
    private readonly TripLifecycleOptions _lifecycleOptions = lifecycleOptions?.Value ?? new TripLifecycleOptions();
    public async Task<CreateTripResult> ExecuteAsync(CreateTripCommand cmd, CancellationToken ct)
    {
        ValidateCommand(cmd);

        var requestHash = ComputeRequestHash(cmd);
        var scope = new CommandScope(cmd.Actor.Id, "rider", "create", null);

        // 1. Replay check
        var existingReceipt = await receiptStore.FindAsync(scope, cmd.CommandKey, ct);
        if (existingReceipt is not null)
        {
            if (!string.Equals(existingReceipt.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException("Command key reused with different payload.");
            }

            if (!string.IsNullOrEmpty(existingReceipt.ResponseJson) && Guid.TryParse(existingReceipt.ResponseJson, out var existingTripId))
            {
                var existingTrip = await reader.LoadAsync(existingTripId, ct);
                if (existingTrip is not null)
                {
                    var isPending = existingTrip.Trip.Status == TripStatus.Requested;
                    return new CreateTripResult(TripMapper.ToReadModel(existingTrip), IsReplay: true, IsPendingWork: isPending);
                }
            }
        }

        // 2. Active rider slot check
        var activeTrip = await queryReader.GetCurrentAsync(cmd.Actor, ct);
        if (activeTrip is not null)
        {
            throw new ActiveTripConflictException(activeTrip.Trip.Id, "Rider already has an active trip.");
        }

        // 3. Create domain trip and persist Requested intent
        var now = clock.GetUtcNow();
        var tripId = Guid.CreateVersion7();
        var trip = Trip.Request(tripId, cmd.Actor.Id, _lifecycleOptions.MaxDriverRejections);

        var normalizedMethod = cmd.PaymentMethod.ToLowerInvariant();
        var mockAlias = normalizedMethod == "card" ? "card.success.v1" : "wallet.success.v1";

        var effectiveTransport = !string.IsNullOrWhiteSpace(cmd.PaymentTransport)
            ? cmd.PaymentTransport.Trim()
            : _lifecycleOptions.PaymentTransport;

        if (string.Equals(effectiveTransport, "Events", StringComparison.OrdinalIgnoreCase) &&
            (eventWriter is null || !eventWriter.IsAvailable))
        {
            effectiveTransport = "Rest";
        }

        var context = new TripContext(
            trip,
            cmd.PickupName ?? "",
            cmd.PickupAddress ?? "",
            cmd.Pickup.Latitude,
            cmd.Pickup.Longitude,
            cmd.DestinationName ?? "",
            cmd.DestinationAddress ?? "",
            cmd.Destination.Latitude,
            cmd.Destination.Longitude,
            normalizedMethod,
            mockAlias,
            null,
            null,
            now,
            now,
            rowVersion: null,
            paymentTransport: effectiveTransport,
            pricingMode: cmd.Mode?.Trim() ?? "standard");

        writer.Add(context);
        assignmentStore.ClaimRider(cmd.Actor.Id, trip.Id);

        var receipt = new CommandReceipt(
            scope.ToScopeString(),
            cmd.CommandKey,
            requestHash,
            trip.Id.ToString("D"),
            "pending",
            0,
            null,
            now,
            now);
        receiptStore.Add(receipt);

        await unitOfWork.SaveAsync(ct);

        // 4. Execute matching immediately within request budget
        var matched = await coordinator.ExecuteInitialMatchingAsync(context, receipt, ct);

        return new CreateTripResult(TripMapper.ToReadModel(context), IsReplay: false, IsPendingWork: !matched);
    }

    public static string ComputeRequestHash(CreateTripCommand cmd)
    {
        var parts = new[]
        {
            "create",
            cmd.Actor.Id.Trim(),
            cmd.Pickup.Latitude.ToString("F6", CultureInfo.InvariantCulture),
            cmd.Pickup.Longitude.ToString("F6", CultureInfo.InvariantCulture),
            cmd.Destination.Latitude.ToString("F6", CultureInfo.InvariantCulture),
            cmd.Destination.Longitude.ToString("F6", CultureInfo.InvariantCulture),
            cmd.PaymentMethod.Trim().ToLowerInvariant(),
            cmd.Mode.Trim().ToLowerInvariant()
        };

        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            sb.Append(p.Length).Append(':').Append(p).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private void ValidateCommand(CreateTripCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (!cmd.Actor.IsRider)
            throw new UnauthorizedTripAccessException("Only riders can create trips.");
        if (string.IsNullOrWhiteSpace(cmd.Actor.Id) || cmd.Actor.Id.Length > 100)
            throw new InvalidTripCommandException("Actor ID is required and must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(cmd.CommandKey))
            throw new InvalidTripCommandException("Idempotency key is required.");

        ValidateCoordinate(cmd.Pickup.Latitude, cmd.Pickup.Longitude, "Pickup");
        ValidateCoordinate(cmd.Destination.Latitude, cmd.Destination.Longitude, "Destination");

        if (Math.Abs(cmd.Pickup.Latitude - cmd.Destination.Latitude) < 1e-6 &&
            Math.Abs(cmd.Pickup.Longitude - cmd.Destination.Longitude) < 1e-6)
        {
            throw new InvalidTripCommandException("Pickup and destination coordinates cannot be identical.");
        }

        var method = cmd.PaymentMethod?.Trim().ToLowerInvariant();
        if (method is not ("card" or "wallet"))
        {
            throw new InvalidTripCommandException("Payment method must be 'card' or 'wallet'.");
        }

        var mode = cmd.Mode?.Trim() ?? "standard";
        if (!_lifecycleOptions.SupportedPricingModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidTripCommandException($"Ride mode '{cmd.Mode}' is not supported. Supported modes: {string.Join(", ", _lifecycleOptions.SupportedPricingModes)}.");
        }

        var requestedTransport = cmd.PaymentTransport?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedTransport))
        {
            if (!string.Equals(requestedTransport, "Rest", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(requestedTransport, "Events", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidTripCommandException($"Unsupported payment transport '{cmd.PaymentTransport}'. Supported values: 'Rest', 'Events'.");
            }

            if (string.Equals(requestedTransport, "Events", StringComparison.OrdinalIgnoreCase) &&
                (eventWriter is null || !eventWriter.IsAvailable))
            {
                throw new InvalidTripCommandException("Event-driven payment transport 'Events' is not supported when messaging is disabled on this server. Supported payment transport: 'Rest'.");
            }
        }
    }

    private static void ValidateCoordinate(double lat, double lon, string name)
    {
        if (double.IsNaN(lat) || double.IsInfinity(lat) || lat < -90.0 || lat > 90.0)
            throw new InvalidTripCommandException($"{name} latitude must be between -90 and 90.");
        if (double.IsNaN(lon) || double.IsInfinity(lon) || lon < -180.0 || lon > 180.0)
            throw new InvalidTripCommandException($"{name} longitude must be between -180 and 180.");
    }
}
