using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

public class TripWorkRecoveryTests
{
    private readonly Mock<ITripReader> _reader = new();
    private readonly Mock<ITripWriter> _writer = new();
    private readonly Mock<ITripUnitOfWork> _uow = new();
    private readonly Mock<ICommandReceiptStore> _receiptStore = new();
    private readonly Mock<IActiveAssignmentStore> _assignmentStore = new();
    private readonly Mock<IMatchingClient> _matchingClient = new();

    private TripWorkCoordinator CreateCoordinator()
    {
        return new TripWorkCoordinator(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _receiptStore.Object,
            _assignmentStore.Object,
            _matchingClient.Object,
            TimeProvider.System,
            NullLogger<TripWorkCoordinator>.Instance);
    }

    [Fact]
    public async Task ResumeMatching_WhenLeased_MatchesAndQuotesSuccessfully()
    {
        var coordinator = CreateCoordinator();
        var tripId = Guid.NewGuid();
        var lease = new TripWorkLease(
            TripWorkKind.Matching,
            Guid.NewGuid(),
            tripId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            1);

        var trip = Trip.Request(tripId, "rider-1", 3);
        var context = new TripContext(
            trip, "PName", "PAddr", 10.76, 106.66, "DName", "DAddr", 10.78, 106.70,
            "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _reader
            .Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-99", 2.0)));

        _matchingClient
            .Setup(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotedFare(Guid.NewGuid(), 60_000m, "VND", 4.0m, 12.0m, DateTimeOffset.UtcNow));

        var success = await coordinator.ResumeMatchingAsync(lease, CancellationToken.None);

        Assert.True(success);
        Assert.Equal("Quoted", trip.Status.ToString());
        Assert.Equal("drv-99", trip.CandidateDriverId);
        _writer.Verify(w => w.Update(context), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailMatchingExhausted_TransitionsTripToFailed_AndReleasesActiveRider()
    {
        var coordinator = CreateCoordinator();
        var tripId = Guid.NewGuid();
        var lease = new TripWorkLease(
            TripWorkKind.Matching,
            Guid.NewGuid(),
            tripId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            5);

        var trip = Trip.Request(tripId, "rider-exhausted-1", 3);
        var context = new TripContext(
            trip, "PName", "PAddr", 10.76, 106.66, "DName", "DAddr", 10.78, 106.70,
            "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _reader
            .Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        await coordinator.FailMatchingExhaustedAsync(lease, CancellationToken.None);

        Assert.Equal(TripStatus.Failed, trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(tripId), Times.Once);
        _writer.Verify(w => w.Update(context), Times.Once);
    }

    [Fact]
    public async Task FailMatchingExhausted_WhenTripAlreadyQuoted_DoesNotChangeTripStatus_AndDoesNotReleaseRider()
    {
        var coordinator = CreateCoordinator();
        var tripId = Guid.NewGuid();
        var lease = new TripWorkLease(
            TripWorkKind.Matching,
            Guid.NewGuid(),
            tripId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            5);

        var trip = Trip.Request(tripId, "rider-quoted-1", 3);
        trip.ApplyQuote(new SwiftRide.TripService.Domain.ValueObjects.FareSnapshot(Guid.NewGuid(), 50000m, "VND"), "drv-1");
        var context = new TripContext(
            trip, "PName", "PAddr", 10.76, 106.66, "DName", "DAddr", 10.78, 106.70,
            "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _reader
            .Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        await coordinator.FailMatchingExhaustedAsync(lease, CancellationToken.None);

        Assert.Equal(TripStatus.Quoted, trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(It.IsAny<Guid>()), Times.Never);
        _writer.Verify(w => w.Update(It.IsAny<TripContext>()), Times.Never);
    }

    [Fact]
    public async Task FailMatchingExhausted_WhenTripAlreadyCancelled_DoesNotChangeTripStatus_AndDoesNotReleaseRider()
    {
        var coordinator = CreateCoordinator();
        var tripId = Guid.NewGuid();
        var lease = new TripWorkLease(
            TripWorkKind.Matching,
            Guid.NewGuid(),
            tripId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            5);

        var trip = Trip.Request(tripId, "rider-cancel-1", 3);
        trip.Cancel("rider-cancel-1");
        var context = new TripContext(
            trip, "PName", "PAddr", 10.76, 106.66, "DName", "DAddr", 10.78, 106.70,
            "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _reader
            .Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        await coordinator.FailMatchingExhaustedAsync(lease, CancellationToken.None);

        Assert.Equal(TripStatus.Cancelled, trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(It.IsAny<Guid>()), Times.Never);
        _writer.Verify(w => w.Update(It.IsAny<TripContext>()), Times.Never);
    }

    [Fact]
    public async Task ResumeMatching_WhenTripAlreadyQuoted_ReturnsTrue_WithoutPerformingMatching()
    {
        var coordinator = CreateCoordinator();
        var tripId = Guid.NewGuid();
        var lease = new TripWorkLease(
            TripWorkKind.Matching,
            Guid.NewGuid(),
            tripId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            1);

        var trip = Trip.Request(tripId, "rider-already-quoted", 3);
        trip.ApplyQuote(new SwiftRide.TripService.Domain.ValueObjects.FareSnapshot(Guid.NewGuid(), 50000m, "VND"), "drv-existing");
        var context = new TripContext(
            trip, "PName", "PAddr", 10.76, 106.66, "DName", "DAddr", 10.78, 106.70,
            "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _reader
            .Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var success = await coordinator.ResumeMatchingAsync(lease, CancellationToken.None);

        Assert.True(success);
        _matchingClient.Verify(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()), Times.Never);
        _matchingClient.Verify(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResumeMatching_WhenTripAlreadyCancelled_ReturnsTrue_WithoutPerformingMatching()
    {
        var coordinator = CreateCoordinator();
        var tripId = Guid.NewGuid();
        var lease = new TripWorkLease(
            TripWorkKind.Matching,
            Guid.NewGuid(),
            tripId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            1);

        var trip = Trip.Request(tripId, "rider-already-cancelled", 3);
        trip.Cancel("rider-already-cancelled");
        var context = new TripContext(
            trip, "PName", "PAddr", 10.76, 106.66, "DName", "DAddr", 10.78, 106.70,
            "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _reader
            .Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var success = await coordinator.ResumeMatchingAsync(lease, CancellationToken.None);

        Assert.True(success);
        _matchingClient.Verify(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()), Times.Never);
        _matchingClient.Verify(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResumeMatching_UsesSnapshottedPricingMode_WhenRequestingQuote()
    {
        var coordinator = CreateCoordinator();
        var tripId = Guid.NewGuid();
        var lease = new TripWorkLease(
            TripWorkKind.Matching,
            Guid.NewGuid(),
            tripId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            1);

        var trip = Trip.Request(tripId, "rider-premium-1", 3);
        var context = new TripContext(
            trip, "PName", "PAddr", 10.76, 106.66, "DName", "DAddr", 10.78, 106.70,
            "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            pricingMode: "premium");

        _reader
            .Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-99", 2.0)));

        _matchingClient
            .Setup(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotedFare(Guid.NewGuid(), 90000m, "VND", 4.0m, 12.0m, DateTimeOffset.UtcNow));

        var success = await coordinator.ResumeMatchingAsync(lease, CancellationToken.None);

        Assert.True(success);
        _matchingClient.Verify(m => m.QuoteAsync(It.Is<QuoteInput>(q => q.Mode == "premium"), It.IsAny<CancellationToken>()), Times.Once);
    }
}