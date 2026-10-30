using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

public class TripAuthorizationTests
{
    private readonly Mock<ITripReader> _reader = new();
    private readonly Mock<ITripWriter> _writer = new();
    private readonly Mock<ITripUnitOfWork> _uow = new();
    private readonly Mock<ICommandReceiptStore> _receiptStore = new();
    private readonly Mock<IActiveAssignmentStore> _assignmentStore = new();
    private readonly Mock<ITripWorkStore> _workStore = new();
    private readonly Mock<IMatchingClient> _matchingClient = new();

    private TripCommandUseCase CreateSut()
    {
        return new TripCommandUseCase(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _receiptStore.Object,
            _assignmentStore.Object,
            _workStore.Object,
            _matchingClient.Object,
            TimeProvider.System,
            NullLogger<TripCommandUseCase>.Instance);
    }

    private static TripContext CreateTripContext(
        TripStatus status,
        string riderId = "rider-1",
        string? offeredDriverId = "drv-1",
        string? assignedDriverId = null)
    {
        var fare = status != TripStatus.Requested ? new FareSnapshot(Guid.NewGuid(), 50_000m, "VND") : null;
        var isLocked = status is not TripStatus.Requested and not TripStatus.Quoted;

        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            riderId,
            status,
            fare,
            isLocked,
            candidateDriverId: "drv-1",
            offeredDriverId: offeredDriverId,
            assignedDriverId: assignedDriverId,
            0,
            3,
            Array.Empty<string>(),
            null,
            null,
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
    public async Task NonExistentTrip_ThrowsTripNotFoundException()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TripContext?)null);

        await Assert.ThrowsAsync<TripNotFoundException>(() =>
            sut.ExecuteAsync(
                new TripActor("rider-1", "rider"),
                tripId,
                new CancelTripCommand(),
                "key-1",
                CancellationToken.None));
    }

    [Fact]
    public async Task Rider_ExecutingDriverCommand_ThrowsUnauthorizedTripAccess()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.DriverOffered);
        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        await Assert.ThrowsAsync<UnauthorizedTripAccessException>(() =>
            sut.ExecuteAsync(
                new TripActor("rider-1", "rider"),
                ctx.Trip.Id,
                new DriverAcceptCommand(),
                "key-1",
                CancellationToken.None));
    }

    [Fact]
    public async Task Driver_ExecutingRiderCommand_ThrowsUnauthorizedTripAccess()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.Quoted);
        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        await Assert.ThrowsAsync<UnauthorizedTripAccessException>(() =>
            sut.ExecuteAsync(
                new TripActor("drv-1", "driver"),
                ctx.Trip.Id,
                new ConfirmQuoteCommand(ctx.Trip.Fare!.QuoteId),
                "key-1",
                CancellationToken.None));
    }

    [Fact]
    public async Task DifferentRider_ConfirmingQuote_ThrowsUnauthorizedTripAccess()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.Quoted, riderId: "owner-rider");
        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        await Assert.ThrowsAsync<UnauthorizedTripAccessException>(() =>
            sut.ExecuteAsync(
                new TripActor("attacker-rider", "rider"),
                ctx.Trip.Id,
                new ConfirmQuoteCommand(ctx.Trip.Fare!.QuoteId),
                "key-1",
                CancellationToken.None));
    }

    [Fact]
    public async Task DifferentDriver_AcceptingOffer_ThrowsUnauthorizedTripAccess()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.DriverOffered, offeredDriverId: "offered-drv");
        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        await Assert.ThrowsAsync<UnauthorizedTripAccessException>(() =>
            sut.ExecuteAsync(
                new TripActor("different-drv", "driver"),
                ctx.Trip.Id,
                new DriverAcceptCommand(),
                "key-1",
                CancellationToken.None));
    }

    [Fact]
    public async Task NonAssignedDriver_ProgressingTrip_ThrowsUnauthorizedTripAccess()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.DriverAccepted, assignedDriverId: "assigned-drv");
        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        await Assert.ThrowsAsync<UnauthorizedTripAccessException>(() =>
            sut.ExecuteAsync(
                new TripActor("random-drv", "driver"),
                ctx.Trip.Id,
                new EnRouteCommand(),
                "key-1",
                CancellationToken.None));
    }
}
