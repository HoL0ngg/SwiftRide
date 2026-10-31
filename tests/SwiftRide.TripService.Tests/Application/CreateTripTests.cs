using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Messaging;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

public class CreateTripTests
{
    private readonly Mock<ITripReader> _reader = new();
    private readonly Mock<ITripWriter> _writer = new();
    private readonly Mock<ITripUnitOfWork> _uow = new();
    private readonly Mock<ICommandReceiptStore> _receiptStore = new();
    private readonly Mock<IActiveAssignmentStore> _assignmentStore = new();
    private readonly Mock<ITripQueryReader> _queryReader = new();
    private readonly Mock<IMatchingClient> _matchingClient = new();

    private readonly TripActor _rider = new("rider-1", "rider");
    private readonly GeoCoordinate _pickup = new(10.762622, 106.660172);
    private readonly GeoCoordinate _dest = new(10.776889, 106.700806);

    private CreateTripUseCase CreateSut(TripLifecycleOptions? lifecycleOptions = null, ITripEventWriter? eventWriter = null)
    {
        var coordinator = new TripWorkCoordinator(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _receiptStore.Object,
            _assignmentStore.Object,
            _matchingClient.Object,
            TimeProvider.System,
            NullLogger<TripWorkCoordinator>.Instance);

        return new CreateTripUseCase(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _receiptStore.Object,
            _assignmentStore.Object,
            _queryReader.Object,
            coordinator,
            TimeProvider.System,
            lifecycleOptions != null ? Microsoft.Extensions.Options.Options.Create(lifecycleOptions) : null,
            eventWriter);
    }

    private CreateTripCommand ValidCommand(string key = "key-1", string? riderId = null) => new(
        new TripActor(riderId ?? _rider.Id, "rider"),
        key,
        _pickup,
        "Ben Thanh",
        "District 1",
        _dest,
        "Landmark 81",
        "Binh Thanh",
        "card",
        "standard");

    [Fact]
    public async Task Create_PersistsRequested_ThenMatchesAndQuotes_TransitionsToQuoted()
    {
        var sut = CreateSut();
        var cmd = ValidCommand();

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-1", 1.5)));

        var quoteId = Guid.NewGuid();
        _matchingClient
            .Setup(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotedFare(quoteId, 50_000m, "VND", 5.2m, 14.5m, DateTimeOffset.UtcNow));

        var result = await sut.ExecuteAsync(cmd, CancellationToken.None);

        Assert.False(result.IsReplay);
        Assert.False(result.IsPendingWork);
        Assert.Equal("Quoted", result.Trip.Status);
        Assert.Equal("drv-1", result.Trip.CandidateDriverId);
        Assert.Equal("50000", result.Trip.FareAmount);
        Assert.Equal("VND", result.Trip.Currency);
        Assert.Equal(5.2m, result.Trip.DistanceKm);
        Assert.Equal(14.5m, result.Trip.EstimatedMinutes);

        // Verified rider claimed
        _assignmentStore.Verify(a => a.ClaimRider(_rider.Id, result.Trip.Id), Times.Once);
        // Intent committed before HTTP, then updated with quote
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task Create_NoDriverAvailable_TransitionsToFailed_AndReleasesRiderSlot()
    {
        var sut = CreateSut();
        var cmd = ValidCommand();

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.NoDriverAvailable, null));

        var result = await sut.ExecuteAsync(cmd, CancellationToken.None);

        Assert.Equal("Failed", result.Trip.Status);
        Assert.Null(result.Trip.CandidateDriverId);
        Assert.Null(result.Trip.FareAmount);

        // Released rider slot on Failed
        _assignmentStore.Verify(a => a.ReleaseRider(result.Trip.Id), Times.Once);
    }

    [Fact]
    public async Task Create_MatchingTimeout_PreservesRequested_AndSchedulesRetry()
    {
        var sut = CreateSut();
        var cmd = ValidCommand();

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceClientTransientException("Matching service unavailable."));

        var result = await sut.ExecuteAsync(cmd, CancellationToken.None);

        // Must stay Requested (NOT marked Failed!)
        Assert.Equal("Requested", result.Trip.Status);
        Assert.True(result.IsPendingWork);

        // Rider slot NOT released
        _assignmentStore.Verify(a => a.ReleaseRider(It.IsAny<Guid>()), Times.Never);
        // Receipt updated to pending with retry attempt
        _receiptStore.Verify(r => r.Update(It.Is<CommandReceipt>(cr => cr.Status == "pending" && cr.Attempts >= 1)), Times.Once);
    }

    [Fact]
    public async Task Create_ReplaySameKey_ReturnsOriginalTrip()
    {
        var sut = CreateSut();
        var cmd = ValidCommand(key: "key-replay");

        var existingTripId = Guid.NewGuid();
        var trip = Trip.Request(existingTripId, _rider.Id, 3);
        var tripContext = new TripContext(
            trip, "Ben Thanh", "D1", _pickup.Latitude, _pickup.Longitude,
            "LM81", "BT", _dest.Latitude, _dest.Longitude,
            "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var requestHash = CreateTripUseCase.ComputeRequestHash(cmd);
        var receipt = new CommandReceipt(
            new CommandScope(_rider.Id, "rider", "create", null).ToScopeString(),
            "key-replay",
            requestHash,
            existingTripId.ToString("D"),
            "completed",
            0,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        _receiptStore
            .Setup(r => r.FindAsync(It.IsAny<CommandScope>(), "key-replay", It.IsAny<CancellationToken>()))
            .ReturnsAsync(receipt);

        _reader
            .Setup(r => r.LoadAsync(existingTripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tripContext);

        var result = await sut.ExecuteAsync(cmd, CancellationToken.None);

        Assert.True(result.IsReplay);
        Assert.Equal(existingTripId, result.Trip.Id);
        _matchingClient.Verify(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ReplayDifferentPayload_ThrowsConflict()
    {
        var sut = CreateSut();
        var cmd = ValidCommand(key: "key-conflict");

        var receipt = new CommandReceipt(
            new CommandScope(_rider.Id, "rider", "create", null).ToScopeString(),
            "key-conflict",
            "different-hash-abc",
            Guid.NewGuid().ToString("D"),
            "completed",
            0,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        _receiptStore
            .Setup(r => r.FindAsync(It.IsAny<CommandScope>(), "key-conflict", It.IsAny<CancellationToken>()))
            .ReturnsAsync(receipt);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => sut.ExecuteAsync(cmd, CancellationToken.None));
    }

    [Fact]
    public async Task Create_RiderAlreadyHasActiveTrip_ThrowsConflict()
    {
        var sut = CreateSut();
        var cmd = ValidCommand(key: "key-active");

        var existingTrip = Trip.Request(Guid.NewGuid(), _rider.Id, 3);
        var existingContext = new TripContext(
            existingTrip, "A", "B", 10, 106, "C", "D", 11, 107, "card", "card.success.v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _queryReader
            .Setup(q => q.GetCurrentAsync(It.Is<TripActor>(a => a.Id == _rider.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingContext);

        await Assert.ThrowsAsync<ActiveTripConflictException>(() => sut.ExecuteAsync(cmd, CancellationToken.None));
    }

    [Fact]
    public async Task Create_WithConfiguredMaxDriverRejections_UsesConfiguredBudgetAndFailsOnSecondRejection()
    {
        var options = Options.Create(new TripLifecycleOptions { MaxDriverRejections = 1 });
        var coordinator = new TripWorkCoordinator(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _receiptStore.Object,
            _assignmentStore.Object,
            _matchingClient.Object,
            TimeProvider.System,
            NullLogger<TripWorkCoordinator>.Instance);

        var sut = new CreateTripUseCase(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _receiptStore.Object,
            _assignmentStore.Object,
            _queryReader.Object,
            coordinator,
            TimeProvider.System,
            options);

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("driver-1", 1.5)));

        var quoteId = Guid.NewGuid();
        _matchingClient
            .Setup(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotedFare(quoteId, 50_000m, "VND", 5.2m, 14.5m, DateTimeOffset.UtcNow));

        TripContext? capturedContext = null;
        _writer.Setup(w => w.Add(It.IsAny<TripContext>()))
            .Callback<TripContext>(c => capturedContext = c);

        var result = await sut.ExecuteAsync(ValidCommand(key: "key-max-rej"), CancellationToken.None);

        Assert.NotNull(capturedContext);
        Assert.Equal(1, capturedContext.Trip.MaxDriverRejections);

        // Move to Offered
        capturedContext.Trip.ConfirmQuote(_rider.Id, quoteId, "driver-1");
        Assert.Equal(TripStatus.DriverOffered, capturedContext.Trip.Status);

        // First rejection: count = 1 <= 1 -> rematches to DriverOffered
        capturedContext.Trip.RejectDriver("driver-1");
        capturedContext.Trip.OfferReplacementDriver("driver-2");
        Assert.Equal(1, capturedContext.Trip.RejectionCount);
        Assert.Equal(TripStatus.DriverOffered, capturedContext.Trip.Status);

        // Second rejection: count = 2 > 1 -> transitions to Failed!
        capturedContext.Trip.RejectDriver("driver-2");
        Assert.Equal(2, capturedContext.Trip.RejectionCount);
        Assert.Equal(TripStatus.Failed, capturedContext.Trip.Status);
    }

    [Fact]
    public async Task Create_WithUnsupportedPricingMode_ThrowsInvalidTripCommandException()
    {
        var sut = CreateSut();
        var cmd = new CreateTripCommand(
            _rider,
            "key-mode",
            _pickup,
            "Ben Thanh",
            "District 1",
            _dest,
            "Landmark 81",
            "Binh Thanh",
            "card",
            "unsupported-mode");

        var ex = await Assert.ThrowsAsync<InvalidTripCommandException>(() => sut.ExecuteAsync(cmd, CancellationToken.None));
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public async Task Create_WithConfiguredCustomPricingMode_SavesPricingModeInTripContext()
    {
        var options = new TripLifecycleOptions
        {
            MaxDriverRejections = 3,
            SupportedPricingModes = ["standard", "premium"]
        };
        var sut = CreateSut(lifecycleOptions: options);

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-1", 1.5)));

        _matchingClient
            .Setup(m => m.QuoteAsync(It.Is<QuoteInput>(q => q.Mode == "premium"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotedFare(Guid.NewGuid(), 90000m, "VND", 5.0m, 15.0m, DateTimeOffset.UtcNow));

        var cmd = new CreateTripCommand(
            _rider,
            "key-premium-mode",
            _pickup,
            "Ben Thanh",
            "District 1",
            _dest,
            "Landmark 81",
            "Binh Thanh",
            "card",
            "premium");

        var result = await sut.ExecuteAsync(cmd, CancellationToken.None);

        Assert.NotNull(result);
        _writer.Verify(w => w.Add(It.Is<TripContext>(c => c.PricingMode == "premium")), Times.Once);
    }

    [Fact]
    public async Task Create_WithEventsTransport_WhenMessagingDisabled_ThrowsInvalidTripCommandException()
    {
        // EventWriter with IsAvailable = false (simulating NoOpTripEventWriter when Messaging=None)
        var mockWriter = new Mock<ITripEventWriter>();
        mockWriter.Setup(x => x.IsAvailable).Returns(false);

        var sut = CreateSut(eventWriter: mockWriter.Object);
        var cmd = new CreateTripCommand(
            _rider,
            "key-events-disabled",
            _pickup,
            "Ben Thanh",
            "District 1",
            _dest,
            "Landmark 81",
            "Binh Thanh",
            "card",
            "standard",
            PaymentTransport: "Events");

        var ex = await Assert.ThrowsAsync<InvalidTripCommandException>(() => sut.ExecuteAsync(cmd, CancellationToken.None));
        Assert.Contains("Event-driven payment transport 'Events' is not supported when messaging is disabled", ex.Message);
    }

    [Fact]
    public async Task Create_WithEventsTransport_WhenMessagingEnabled_AcceptedAndStoredInContext()
    {
        var mockWriter = new Mock<ITripEventWriter>();
        mockWriter.Setup(x => x.IsAvailable).Returns(true);

        var sut = CreateSut(eventWriter: mockWriter.Object);

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-1", 1.5)));

        _matchingClient
            .Setup(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotedFare(Guid.NewGuid(), 50_000m, "VND", 5.2m, 14.5m, DateTimeOffset.UtcNow));

        var cmd = new CreateTripCommand(
            _rider,
            "key-events-enabled",
            _pickup,
            "Ben Thanh",
            "District 1",
            _dest,
            "Landmark 81",
            "Binh Thanh",
            "card",
            "standard",
            PaymentTransport: "Events");

        var result = await sut.ExecuteAsync(cmd, CancellationToken.None);

        Assert.NotNull(result);
        _writer.Verify(w => w.Add(It.Is<TripContext>(c => c.PaymentTransport == "Events")), Times.Once);
    }

    [Fact]
    public async Task Create_WithInvalidPaymentTransport_ThrowsInvalidTripCommandException()
    {
        var sut = CreateSut();
        var cmd = new CreateTripCommand(
            _rider,
            "key-invalid-transport",
            _pickup,
            "Ben Thanh",
            "District 1",
            _dest,
            "Landmark 81",
            "Binh Thanh",
            "card",
            "standard",
            PaymentTransport: "Bluetooth");

        var ex = await Assert.ThrowsAsync<InvalidTripCommandException>(() => sut.ExecuteAsync(cmd, CancellationToken.None));
        Assert.Contains("Unsupported payment transport 'Bluetooth'", ex.Message);
    }

    [Fact]
    public async Task Create_DefaultRestTransport_WhenClientOmitsTransport_UsesRest()
    {
        var sut = CreateSut();

        _matchingClient
            .Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-1", 1.5)));

        _matchingClient
            .Setup(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotedFare(Guid.NewGuid(), 50_000m, "VND", 5.2m, 14.5m, DateTimeOffset.UtcNow));

        var cmd = new CreateTripCommand(
            _rider,
            "key-default-transport",
            _pickup,
            "Ben Thanh",
            "District 1",
            _dest,
            "Landmark 81",
            "Binh Thanh",
            "card",
            "standard",
            PaymentTransport: null);

        var result = await sut.ExecuteAsync(cmd, CancellationToken.None);

        Assert.NotNull(result);
        _writer.Verify(w => w.Add(It.Is<TripContext>(c => c.PaymentTransport == "Rest")), Times.Once);
    }
}