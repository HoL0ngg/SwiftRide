using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;

namespace SwiftRide.TripService.Application.Trips;

public sealed class TripQueryService(
    ITripQueryReader queryReader,
    ITripReader tripReader)
{
    public async Task<TripReadModel?> GetCurrentTripAsync(TripActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var ctx = await queryReader.GetCurrentAsync(actor, ct);
        return ctx is not null ? TripMapper.ToReadModel(ctx) : null;
    }

    public async Task<DriverFeedResponse> GetDriverFeedAsync(TripActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.Role != "driver")
        {
            throw new UnauthorizedTripAccessException("Only drivers have access to driver feed.");
        }

        var feed = await queryReader.GetDriverFeedAsync(actor, ct);

        var offered = feed.OfferedTrip is not null ? TripMapper.ToReadModel(feed.OfferedTrip) : null;
        var active = feed.ActiveTrip is not null ? TripMapper.ToReadModel(feed.ActiveTrip) : null;

        return new DriverFeedResponse(offered, active);
    }

    public async Task<TripHistoryPageResponse> GetHistoryAsync(
        TripActor actor,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var clampedLimit = Math.Clamp(limit, 1, 100);
        var page = await queryReader.GetHistoryAsync(actor, cursor, clampedLimit, ct);

        var items = page.Items.Select(TripMapper.ToReadModel).ToList();
        return new TripHistoryPageResponse(items, page.NextCursor);
    }

    public async Task<TripReadModel?> GetTripByIdAsync(TripActor actor, Guid tripId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var ctx = await tripReader.LoadAsync(tripId, ct);
        if (ctx is null)
        {
            return null;
        }

        var trip = ctx.Trip;
        if (actor.Role == "rider" && trip.RiderId != actor.Id)
        {
            throw new UnauthorizedTripAccessException("Riders can only view their own trips.");
        }

        if (actor.Role == "driver" && trip.AssignedDriverId != actor.Id && trip.OfferedDriverId != actor.Id)
        {
            throw new UnauthorizedTripAccessException("Drivers can only view trips offered to or assigned to them.");
        }

        return TripMapper.ToReadModel(ctx);
    }
}
