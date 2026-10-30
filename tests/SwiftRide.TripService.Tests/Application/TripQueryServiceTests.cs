using Moq;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

public class TripQueryServiceTests
{
    private readonly Mock<ITripQueryReader> _queryReader = new();
    private readonly Mock<ITripReader> _tripReader = new();

    private TripQueryService CreateSut() => new(_queryReader.Object, _tripReader.Object);

    private static TripContext CreateTripContext(TripStatus status, string riderId = "rider-1", string? assignedDriverId = null, string? offeredDriverId = null)
    {
        var fare = status != TripStatus.Requested ? new FareSnapshot(Guid.NewGuid(), 50_000m, "VND") : null;
        var isLocked = status is not TripStatus.Requested and not TripStatus.Quoted;

        var paymentId = status == TripStatus.Paid ? Guid.NewGuid() : (Guid?)null;

        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            riderId,
            status,
            fare,
            isLocked,
            candidateDriverId: "drv-cand",
            offeredDriverId: offeredDriverId,
            assignedDriverId: assignedDriverId,
            0,
            3,
            Array.Empty<string>(),
            paymentId != null ? $"trip-{paymentId.Value:D}-payment" : null,
            paymentId,
            null);

        var trip = Trip.Restore(snapshot);
        return new TripContext(
            trip,
            "Pickup", "District 1", 10.76, 106.66,
            "Dest", "District 2", 10.78, 106.72,
            "card", "card.success.v1",
            5.0m, 15.0m,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GetCurrentTrip_ReturnsReadModel_WhenExists()
    {
        var sut = CreateSut();
        var actor = new TripActor("rider-1", "rider");
        var ctx = CreateTripContext(TripStatus.Quoted, riderId: "rider-1");

        _queryReader.Setup(q => q.GetCurrentAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var result = await sut.GetCurrentTripAsync(actor, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Quoted", result.Status);
        Assert.Equal("rider-1", result.RiderId);
    }

    [Fact]
    public async Task GetDriverFeed_DriverActor_ReturnsOfferedAndActiveTrips()
    {
        var sut = CreateSut();
        var actor = new TripActor("drv-1", "driver");
        var offeredCtx = CreateTripContext(TripStatus.DriverOffered, offeredDriverId: "drv-1");
        var activeCtx = CreateTripContext(TripStatus.DriverAccepted, assignedDriverId: "drv-1");

        _queryReader.Setup(q => q.GetDriverFeedAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverFeedReadModel(offeredCtx, activeCtx));

        var feed = await sut.GetDriverFeedAsync(actor, CancellationToken.None);

        Assert.NotNull(feed.OfferedTrip);
        Assert.Equal("DriverOffered", feed.OfferedTrip.Status);
        Assert.NotNull(feed.ActiveTrip);
        Assert.Equal("DriverAccepted", feed.ActiveTrip.Status);
    }

    [Fact]
    public async Task GetDriverFeed_RiderActor_ThrowsUnauthorizedTripAccess()
    {
        var sut = CreateSut();
        var actor = new TripActor("rider-1", "rider");

        await Assert.ThrowsAsync<UnauthorizedTripAccessException>(() =>
            sut.GetDriverFeedAsync(actor, CancellationToken.None));
    }

    [Fact]
    public async Task GetHistory_ClampsLimit_AndReturnsPage()
    {
        var sut = CreateSut();
        var actor = new TripActor("rider-1", "rider");
        var ctx = CreateTripContext(TripStatus.Paid, riderId: "rider-1", assignedDriverId: "drv-1");

        _queryReader.Setup(q => q.GetHistoryAsync(actor, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TripHistoryPageReadModel(new[] { ctx }, "next-cursor-123"));

        // Request with limit 500 (should clamp to 100)
        var result = await sut.GetHistoryAsync(actor, null, 500, CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("next-cursor-123", result.NextCursor);
        _queryReader.Verify(q => q.GetHistoryAsync(actor, null, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTripById_NonExistent_ReturnsNull()
    {
        var sut = CreateSut();
        var actor = new TripActor("rider-1", "rider");
        var tripId = Guid.NewGuid();

        _tripReader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TripContext?)null);

        var result = await sut.GetTripByIdAsync(actor, tripId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTripById_RiderViewingOtherTrip_ThrowsUnauthorizedTripAccess()
    {
        var sut = CreateSut();
        var actor = new TripActor("rider-1", "rider");
        var ctx = CreateTripContext(TripStatus.Quoted, riderId: "other-rider");

        _tripReader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        await Assert.ThrowsAsync<UnauthorizedTripAccessException>(() =>
            sut.GetTripByIdAsync(actor, ctx.Trip.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetTripById_DriverViewingUnassignedTrip_ThrowsUnauthorizedTripAccess()
    {
        var sut = CreateSut();
        var actor = new TripActor("drv-1", "driver");
        var ctx = CreateTripContext(TripStatus.DriverAccepted, assignedDriverId: "other-drv");

        _tripReader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        await Assert.ThrowsAsync<UnauthorizedTripAccessException>(() =>
            sut.GetTripByIdAsync(actor, ctx.Trip.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetTripById_AssignedDriver_ReturnsReadModel()
    {
        var sut = CreateSut();
        var actor = new TripActor("drv-1", "driver");
        var ctx = CreateTripContext(TripStatus.DriverAccepted, assignedDriverId: "drv-1");

        _tripReader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var result = await sut.GetTripByIdAsync(actor, ctx.Trip.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("DriverAccepted", result.Status);
    }
}
