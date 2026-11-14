using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Messaging;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

/// <summary>Trip -> Matching over events: no REST call is made, requests are staged in the outbox.</summary>
public class EventMatchingTests
{
    private readonly Mock<ITripReader> _reader = new();
    private readonly Mock<ITripWriter> _writer = new();
    private readonly Mock<ITripUnitOfWork> _uow = new();
    private readonly Mock<ICommandReceiptStore> _receiptStore = new();
    private readonly Mock<IActiveAssignmentStore> _assignmentStore = new();
    private readonly Mock<ITripWorkStore> _workStore = new();
    private readonly Mock<ITripQueryReader> _queryReader = new();
    private readonly Mock<IMatchingClient> _matchingClient = new(MockBehavior.Strict);
    private readonly Mock<ITripEventWriter> _eventWriter = new();

    private readonly TripActor _rider = new("rider-1", "rider");
    private readonly TripActor _driver = new("drv-1", "driver");

    private static IOptions<TripLifecycleOptions> EventOptions() =>
        Microsoft.Extensions.Options.Options.Create(new TripLifecycleOptions { MatchingTransport = "Events" });

    private CreateTripUseCase CreateTripSut(bool messagingAvailable = true, bool eventMatching = true)
    {
        _eventWriter.Setup(w => w.IsAvailable).Returns(messagingAvailable);
        var coordinator = new TripWorkCoordinator(
            _reader.Object, _writer.Object, _uow.Object, _receiptStore.Object, _assignmentStore.Object,
            _matchingClient.Object, TimeProvider.System, NullLogger<TripWorkCoordinator>.Instance);

        return new CreateTripUseCase(
            _reader.Object, _writer.Object, _uow.Object, _receiptStore.Object, _assignmentStore.Object,
            _queryReader.Object, coordinator, TimeProvider.System,
            eventMatching ? EventOptions() : Microsoft.Extensions.Options.Options.Create(new TripLifecycleOptions()),
            _eventWriter.Object);
    }

    private TripCommandUseCase CreateCommandSut(bool messagingAvailable = true)
    {
        _eventWriter.Setup(w => w.IsAvailable).Returns(messagingAvailable);
        return new TripCommandUseCase(
            _reader.Object, _writer.Object, _uow.Object, _receiptStore.Object, _assignmentStore.Object,
            _workStore.Object, _matchingClient.Object, TimeProvider.System,
            NullLogger<TripCommandUseCase>.Instance,
            eventWriter: _eventWriter.Object,
            lifecycleOptions: EventOptions());
    }

    private CreateTripCommand ValidCreate() => new(
        _rider, "key-1", new GeoCoordinate(10.762622, 106.660172), "Ben Thanh", "District 1",
        new GeoCoordinate(10.776889, 106.700806), "Landmark 81", "Binh Thanh", "card", "standard");

    private static TripContext Context(TripStatus status, Guid quoteId, string? offered = null, string[]? rejected = null)
    {
        rejected ??= [];
        var fare = new FareSnapshot(quoteId, 50_000m, "VND");
        var snapshot = new TripSnapshot(
            Guid.NewGuid(), "rider-1", status, fare,
            status != TripStatus.Quoted,
            status == TripStatus.Quoted ? "drv-cand" : null,
            offered,
            null,
            rejected.Length,
            3,
            rejected,
            null,
            null,
            null);
        return new TripContext(Trip.Restore(snapshot), "P", "A", 10.76, 106.66, "D", "A", 10.78, 106.72,
            "card", "card.success.v1", 5m, 15m, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Create_WithEventMatching_StagesQuoteRequest_AndReturnsPendingWithoutCallingMatching()
    {
        var sut = CreateTripSut();
        QuoteRequestedV1? staged = null;
        _eventWriter.Setup(w => w.Add(It.IsAny<QuoteRequestedV1>())).Callback<QuoteRequestedV1>(m => staged = m);
        CommandReceipt? receipt = null;
        _receiptStore.Setup(r => r.Add(It.IsAny<CommandReceipt>())).Callback<CommandReceipt>(r => receipt = r);

        var result = await sut.ExecuteAsync(ValidCreate(), CancellationToken.None);

        Assert.True(result.IsPendingWork);
        Assert.Equal("Requested", result.Trip.Status);
        Assert.NotNull(staged);
        Assert.Equal(result.Trip.Id, staged!.TripId);
        Assert.Equal("standard", staged.Mode);
        Assert.Equal(10.762622, staged.PickupLatitude);
        Assert.Equal(106.700806, staged.DestinationLongitude);
        // The REST matching worker must not pick this trip up: the receipt is completed immediately.
        Assert.Equal("completed", receipt!.Status);
        // Trip row, rider claim and outbox row are committed together, exactly once.
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
        _matchingClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Create_WithEventMatchingConfigured_ButMessagingDisabled_FallsBackToRest()
    {
        var sut = CreateTripSut(messagingAvailable: false);
        _matchingClient.Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-1", 1.5)));
        _matchingClient.Setup(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotedFare(Guid.NewGuid(), 50_000m, "VND", 5.2m, 14.5m, DateTimeOffset.UtcNow));

        var result = await sut.ExecuteAsync(ValidCreate(), CancellationToken.None);

        Assert.Equal("Quoted", result.Trip.Status);
        _eventWriter.Verify(w => w.Add(It.IsAny<QuoteRequestedV1>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmQuote_WithEventMatching_LocksFare_AndStagesDriverMatchRequest()
    {
        var sut = CreateCommandSut();
        var quoteId = Guid.NewGuid();
        var ctx = Context(TripStatus.Quoted, quoteId);
        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ctx);
        DriverMatchRequestedV1? staged = null;
        _eventWriter.Setup(w => w.Add(It.IsAny<DriverMatchRequestedV1>())).Callback<DriverMatchRequestedV1>(m => staged = m);

        var outcome = await sut.ExecuteAsync(_rider, ctx.Trip.Id, new ConfirmQuoteCommand(quoteId), "confirm-1", CancellationToken.None);

        Assert.Equal("DriverOffered", outcome.Trip.Status);
        Assert.Null(outcome.Trip.OfferedDriverId); // rematch pending until Matching answers
        Assert.True(ctx.Trip.IsFareLocked);
        Assert.NotNull(staged);
        Assert.Equal(ctx.Trip.Id, staged!.TripId);
        Assert.Empty(staged.ExcludedDriverIds);
        _assignmentStore.Verify(a => a.TryClaimDriver(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
        _matchingClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DriverReject_WithEventMatching_StagesRematchExcludingRejectedDrivers()
    {
        var sut = CreateCommandSut();
        var ctx = Context(TripStatus.DriverOffered, Guid.NewGuid(), offered: "drv-1", rejected: ["old-1"]);
        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ctx);
        DriverMatchRequestedV1? staged = null;
        _eventWriter.Setup(w => w.Add(It.IsAny<DriverMatchRequestedV1>())).Callback<DriverMatchRequestedV1>(m => staged = m);

        var outcome = await sut.ExecuteAsync(_driver, ctx.Trip.Id, new DriverRejectCommand(), "reject-1", CancellationToken.None);

        Assert.Equal("DriverOffered", outcome.Trip.Status);
        Assert.Null(outcome.Trip.OfferedDriverId);
        Assert.NotNull(staged);
        Assert.Equal(["old-1", "drv-1"], staged!.ExcludedDriverIds);
        _assignmentStore.Verify(a => a.ReleaseDriver(ctx.Trip.Id), Times.Once);
        _matchingClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DriverReject_AboveLimit_FailsTrip_WithoutStagingRematch()
    {
        var sut = CreateCommandSut();
        var ctx = Context(TripStatus.DriverOffered, Guid.NewGuid(), offered: "drv-1", rejected: ["a", "b", "c"]);
        _reader.Setup(r => r.LoadAsync(ctx.Trip.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ctx);

        var outcome = await sut.ExecuteAsync(_driver, ctx.Trip.Id, new DriverRejectCommand(), "reject-2", CancellationToken.None);

        Assert.Equal("Failed", outcome.Trip.Status);
        _eventWriter.Verify(w => w.Add(It.IsAny<DriverMatchRequestedV1>()), Times.Never);
    }
}
