using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

public class TripCommandTests
{
    private readonly Mock<ITripReader> _reader = new();
    private readonly Mock<ITripWriter> _writer = new();
    private readonly Mock<ITripUnitOfWork> _uow = new();
    private readonly Mock<ICommandReceiptStore> _receiptStore = new();
    private readonly Mock<IActiveAssignmentStore> _assignmentStore = new();
    private readonly Mock<ITripWorkStore> _workStore = new();
    private readonly Mock<IMatchingClient> _matchingClient = new();

    private readonly TripActor _rider = new("rider-1", "rider");
    private readonly TripActor _driver = new("drv-1", "driver");

    private TripCommandUseCase CreateSut()
    {
        _assignmentStore.Setup(a => a.TryClaimDriver(It.IsAny<string>(), It.IsAny<Guid>())).Returns(true);
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

    private static TripContext CreateTripContext(TripStatus status, string riderId = "rider-1", string? candidateDriverId = null, string? offeredDriverId = null, string? assignedDriverId = null, Guid? quoteId = null, int rejections = 0)
    {
        var qId = quoteId ?? Guid.NewGuid();
        var fare = status != TripStatus.Requested ? new FareSnapshot(qId, 50_000m, "VND") : null;
        var isLocked = status is not TripStatus.Requested and not TripStatus.Quoted;
        var candidate = candidateDriverId ?? (status == TripStatus.Quoted ? "drv-cand" : null);

        var rejected = new List<string>();
        for (int i = 0; i < rejections; i++) rejected.Add($"old-drv-{i}");

        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            riderId,
            status,
            fare,
            isLocked,
            candidate,
            offeredDriverId,
            assignedDriverId,
            rejections,
            3,
            rejected,
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
    public async Task ConfirmQuote_ValidQuote_TransitionsToDriverOffered_AndClaimsDriver()
    {
        var sut = CreateSut();
        var quoteId = Guid.NewGuid();
        var ctx = CreateTripContext(TripStatus.Quoted, quoteId: quoteId, candidateDriverId: "drv-1");

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        _matchingClient.Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-1", 1.2)));

        var outcome = await sut.ExecuteAsync(
            _rider,
            ctx.Trip.Id,
            new ConfirmQuoteCommand(quoteId),
            "confirm-key-1",
            CancellationToken.None);

        Assert.False(outcome.IsReplay);
        Assert.Equal("DriverOffered", outcome.Trip.Status);
        Assert.Equal("drv-1", outcome.Trip.OfferedDriverId);

        _assignmentStore.Verify(a => a.TryClaimDriver("drv-1", ctx.Trip.Id), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmQuote_ReplaySameKeyAndQuote_ReturnsReplayOutcome()
    {
        var sut = CreateSut();
        var quoteId = Guid.NewGuid();
        var ctx = CreateTripContext(TripStatus.DriverOffered, quoteId: quoteId, offeredDriverId: "drv-1");

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var payloadBytes = Encoding.UTF8.GetBytes(quoteId.ToString("D"));
        var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes));

        var scope = new CommandScope(_rider.Id, _rider.Role, "confirm-quote", ctx.Trip.Id);
        _receiptStore.Setup(s => s.FindAsync(
                It.Is<CommandScope>(sc => sc.ActorId == _rider.Id && sc.Operation == "confirm-quote"),
                "confirm-key-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandReceipt(
                scope.ToScopeString(),
                "confirm-key-1",
                payloadHash,
                ctx.Trip.Id.ToString("D"),
                "completed",
                1,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        var outcome = await sut.ExecuteAsync(
            _rider,
            ctx.Trip.Id,
            new ConfirmQuoteCommand(quoteId),
            "confirm-key-1",
            CancellationToken.None);

        Assert.True(outcome.IsReplay);
        Assert.Equal("DriverOffered", outcome.Trip.Status);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmQuote_SameKeyDifferentQuote_ThrowsIdempotencyConflict()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.Quoted);

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var scope = new CommandScope(_rider.Id, _rider.Role, "confirm-quote", ctx.Trip.Id);
        _receiptStore.Setup(s => s.FindAsync(
                It.IsAny<CommandScope>(),
                "confirm-key-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandReceipt(
                scope.ToScopeString(),
                "confirm-key-1",
                "DIFFERENT_HASH",
                ctx.Trip.Id.ToString("D"),
                "completed",
                1,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            sut.ExecuteAsync(
                _rider,
                ctx.Trip.Id,
                new ConfirmQuoteCommand(Guid.NewGuid()),
                "confirm-key-1",
                CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmQuote_NoDriverAvailable_TransitionsToFailed_AndReleasesRider()
    {
        var sut = CreateSut();
        var quoteId = Guid.NewGuid();
        var ctx = CreateTripContext(TripStatus.Quoted, quoteId: quoteId, candidateDriverId: "drv-1");

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        _matchingClient.Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.NoDriverAvailable, null));

        var outcome = await sut.ExecuteAsync(
            _rider,
            ctx.Trip.Id,
            new ConfirmQuoteCommand(quoteId),
            "confirm-key-1",
            CancellationToken.None);

        Assert.Equal("Failed", outcome.Trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DriverAccept_AssignedDriver_TransitionsToDriverAccepted()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.DriverOffered, offeredDriverId: _driver.Id);

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(
            _driver,
            ctx.Trip.Id,
            new DriverAcceptCommand(),
            "accept-key-1",
            CancellationToken.None);

        Assert.Equal("DriverAccepted", outcome.Trip.Status);
        Assert.Equal(_driver.Id, outcome.Trip.AssignedDriverId);
        Assert.Null(outcome.Trip.OfferedDriverId);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DriverReject_WithinLimit_ReleasesDriver_AndRematchesNextDriver()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.DriverOffered, offeredDriverId: _driver.Id, rejections: 0);

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        _matchingClient.Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-2", 2.0)));

        var outcome = await sut.ExecuteAsync(
            _driver,
            ctx.Trip.Id,
            new DriverRejectCommand(),
            "reject-key-1",
            CancellationToken.None);

        Assert.Equal("DriverOffered", outcome.Trip.Status);
        Assert.Equal("drv-2", outcome.Trip.OfferedDriverId);
        Assert.Equal(1, outcome.Trip.RejectionCount);

        _assignmentStore.Verify(a => a.ReleaseDriver(ctx.Trip.Id), Times.Once);
        _assignmentStore.Verify(a => a.TryClaimDriver("drv-2", ctx.Trip.Id), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DriverReject_ExceedsLimit_TransitionsToFailed_AndReleasesRider()
    {
        var sut = CreateSut();
        // Trip already has 3 rejections, limit is 3. 4th rejection causes Failed.
        var ctx = CreateTripContext(TripStatus.DriverOffered, offeredDriverId: _driver.Id, rejections: 3);

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(
            _driver,
            ctx.Trip.Id,
            new DriverRejectCommand(),
            "reject-key-1",
            CancellationToken.None);

        Assert.Equal("Failed", outcome.Trip.Status);
        _assignmentStore.Verify(a => a.ReleaseDriver(ctx.Trip.Id), Times.Once);
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnRoute_AssignedDriver_TransitionsToEnRoute()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.DriverAccepted, assignedDriverId: _driver.Id);

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(
            _driver,
            ctx.Trip.Id,
            new EnRouteCommand(),
            "enroute-key-1",
            CancellationToken.None);

        Assert.Equal("EnRoute", outcome.Trip.Status);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pickup_AssignedDriver_TransitionsToPickedUp()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.EnRoute, assignedDriverId: _driver.Id);

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(
            _driver,
            ctx.Trip.Id,
            new PickupCommand(),
            "pickup-key-1",
            CancellationToken.None);

        Assert.Equal("PickedUp", outcome.Trip.Status);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dropoff_AssignedDriver_TransitionsToPaymentPending_ReleasesDriver_AndCreatesPaymentJob()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.PickedUp, assignedDriverId: _driver.Id);

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(
            _driver,
            ctx.Trip.Id,
            new DropoffCommand(),
            "dropoff-key-1",
            CancellationToken.None);

        Assert.Equal("PaymentPending", outcome.Trip.Status);
        // Driver released so driver can take new trips
        _assignmentStore.Verify(a => a.ReleaseDriver(ctx.Trip.Id), Times.Once);
        // Rider NOT released (still pending payment)
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Never);
        // Payment job created
        _workStore.Verify(w => w.AddPaymentJob(ctx.Trip.Id, $"trip-{ctx.Trip.Id:D}-payment", It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>()), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_RiderInQuoted_TransitionsToCancelled_AndReleasesRider()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.Quoted);

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(
            _rider,
            ctx.Trip.Id,
            new CancelTripCommand("Changed mind"),
            "cancel-key-1",
            CancellationToken.None);

        Assert.Equal("Cancelled", outcome.Trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_RiderInDriverOffered_TransitionsToCancelled_AndReleasesRiderAndDriver()
    {
        var sut = CreateSut();
        var ctx = CreateTripContext(TripStatus.DriverOffered, offeredDriverId: "drv-1");

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(
            _rider,
            ctx.Trip.Id,
            new CancelTripCommand("Too long to wait"),
            "cancel-key-1",
            CancellationToken.None);

        Assert.Equal("Cancelled", outcome.Trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Once);
        _assignmentStore.Verify(a => a.ReleaseDriver(ctx.Trip.Id), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmQuote_FirstDriverBusy_RematchesAndOffersSecondDriver()
    {
        var sut = CreateSut();
        var qId = Guid.NewGuid();
        var ctx = CreateTripContext(TripStatus.Quoted, quoteId: qId, candidateDriverId: "drv-busy");

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        // First candidate drv-busy is busy; second candidate drv-free is available
        _assignmentStore.Setup(a => a.TryClaimDriver("drv-busy", ctx.Trip.Id)).Returns(false);
        _assignmentStore.Setup(a => a.TryClaimDriver("drv-free", ctx.Trip.Id)).Returns(true);

        _matchingClient.SetupSequence(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-busy", 1.0)))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-free", 1.5)));

        var outcome = await sut.ExecuteAsync(
            _rider,
            ctx.Trip.Id,
            new ConfirmQuoteCommand(qId),
            "confirm-key-busy-1",
            CancellationToken.None);

        Assert.Equal("DriverOffered", outcome.Trip.Status);
        Assert.Equal("drv-free", outcome.Trip.OfferedDriverId);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmQuote_AllDriversBusy_FailsTripAndReleasesRider()
    {
        var sut = CreateSut();
        var qId = Guid.NewGuid();
        var ctx = CreateTripContext(TripStatus.Quoted, quoteId: qId, candidateDriverId: "drv-busy");

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        // All candidate drivers are busy
        _assignmentStore.Setup(a => a.TryClaimDriver(It.IsAny<string>(), ctx.Trip.Id)).Returns(false);

        _matchingClient.Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-busy-always", 1.0)));

        var outcome = await sut.ExecuteAsync(
            _rider,
            ctx.Trip.Id,
            new ConfirmQuoteCommand(qId),
            "confirm-key-all-busy",
            CancellationToken.None);

        Assert.Equal("Failed", outcome.Trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dropoff_TripHasEventsTransport_WhenEventWriterUnavailable_FallsBackToRest_AndSchedulesRestPaymentJob()
    {
        var mockWriter = new Mock<SwiftRide.TripService.Application.Messaging.ITripEventWriter>();
        mockWriter.Setup(x => x.IsAvailable).Returns(false);

        _assignmentStore.Setup(a => a.TryClaimDriver(It.IsAny<string>(), It.IsAny<Guid>())).Returns(true);
        var sut = new TripCommandUseCase(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _receiptStore.Object,
            _assignmentStore.Object,
            _workStore.Object,
            _matchingClient.Object,
            TimeProvider.System,
            NullLogger<TripCommandUseCase>.Instance,
            paymentOptions: null,
            eventWriter: mockWriter.Object);

        var ctx = CreateTripContext(TripStatus.PickedUp, assignedDriverId: _driver.Id);
        ctx.PaymentTransport = "Events"; // Initially set to Events

        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(
            _driver,
            ctx.Trip.Id,
            new DropoffCommand(),
            "dropoff-fallback-key-1",
            CancellationToken.None);

        Assert.Equal("PaymentPending", outcome.Trip.Status);
        Assert.Equal("Rest", ctx.PaymentTransport); // Transport safely fell back to Rest!
        _workStore.Verify(w => w.AddPaymentJob(ctx.Trip.Id, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), "Rest"), Times.Once);
        mockWriter.Verify(x => x.Add(It.IsAny<SwiftRide.Contracts.V1.RideCompletedV1>()), Times.Never);
    }
}
