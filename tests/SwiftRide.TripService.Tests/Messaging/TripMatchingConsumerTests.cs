using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Consumers;
using SwiftRide.TripService.Infrastructure.Messaging;
using SwiftRide.TripService.Infrastructure.Persistence;
using SwiftRide.TripService.Tests.Infrastructure;
using Xunit;

namespace SwiftRide.TripService.Tests.Messaging;

/// <summary>Trip-side consumers of Matching replies. They need the local SQL Server fixture (inbox + assignments).</summary>
[Collection("TripDatabase")]
public class TripMatchingConsumerTests
{
    private readonly TripDatabaseFixture _fixture;

    public TripMatchingConsumerTests(TripDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static ConsumeContext<T> CreateConsumeContext<T>(T message) where T : class
    {
        var mock = new Mock<ConsumeContext<T>>();
        mock.Setup(c => c.Message).Returns(message);
        mock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return mock.Object;
    }

    /// <summary>Seeds a trip in <c>Requested</c> (or <c>DriverOffered</c> with a pending rematch when confirmQuote is true).</summary>
    private async Task<Guid> SeedTripAsync(string riderId, bool confirmQuote)
    {
        await using var db = _fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var tripId = Guid.NewGuid();
        var trip = Trip.Request(tripId, riderId, 3);
        if (confirmQuote)
        {
            var quoteId = Guid.NewGuid();
            trip.ApplyQuote(new FareSnapshot(quoteId, 50_000m, "VND"), "drv-cand");
            trip.ConfirmQuote(riderId, quoteId);
        }

        new TripRepository(db).Add(new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok", null, null, now, now));
        new ActiveAssignmentStore(db).ClaimRider(riderId, tripId);
        await new TripUnitOfWork(db).SaveAsync(CancellationToken.None);
        return tripId;
    }

    private static QuoteReadyConsumer QuoteReady(TripDbContext db) => new(
        new TripRepository(db), new TripRepository(db), new TripUnitOfWork(db), new ActiveAssignmentStore(db),
        db, TimeProvider.System, NullLogger<QuoteReadyConsumer>.Instance);

    private static QuoteFailedConsumer QuoteFailed(TripDbContext db) => new(
        new TripRepository(db), new TripRepository(db), new TripUnitOfWork(db), new ActiveAssignmentStore(db),
        db, TimeProvider.System, NullLogger<QuoteFailedConsumer>.Instance);

    private static DriverMatchedConsumer DriverMatched(TripDbContext db) => new(
        new TripRepository(db), new TripRepository(db), new TripUnitOfWork(db), new ActiveAssignmentStore(db),
        new TripEventWriter(db), db, TimeProvider.System, NullLogger<DriverMatchedConsumer>.Instance);

    private static QuoteReadyV1 QuoteReadyMessage(Guid tripId, Guid? eventId = null) => new(
        eventId ?? Guid.NewGuid(), tripId, Guid.NewGuid(), 60_000m, "VND", 5.2m, 14.5m, "drv-1", 1.1,
        DateTimeOffset.UtcNow, "corr");

    [LocalSqlServerFact]
    public async Task QuoteReady_MovesRequestedTripToQuoted_AndIgnoresDuplicate()
    {
        var tripId = await SeedTripAsync($"rider-{Guid.NewGuid():N}", confirmQuote: false);
        var message = QuoteReadyMessage(tripId);

        await using (var db = _fixture.CreateDbContext())
            await QuoteReady(db).Consume(CreateConsumeContext(message));
        await using (var db = _fixture.CreateDbContext())
            await QuoteReady(db).Consume(CreateConsumeContext(message)); // redelivery

        await using var verify = _fixture.CreateDbContext();
        var trip = await verify.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("Quoted", trip.Status);
        Assert.Equal(1, await verify.IntegrationInbox.CountAsync(i => i.MessageId == message.EventId));
    }

    [LocalSqlServerFact]
    public async Task QuoteReady_ForTripNoLongerRequested_IsIgnored()
    {
        var tripId = await SeedTripAsync($"rider-{Guid.NewGuid():N}", confirmQuote: true);

        await using (var db = _fixture.CreateDbContext())
            await QuoteReady(db).Consume(CreateConsumeContext(QuoteReadyMessage(tripId)));

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal("DriverOffered", (await verify.Trips.FirstAsync(t => t.Id == tripId)).Status);
    }

    [LocalSqlServerFact]
    public async Task QuoteFailed_FailsRequestedTrip_AndReleasesRider()
    {
        var riderId = $"rider-{Guid.NewGuid():N}";
        var tripId = await SeedTripAsync(riderId, confirmQuote: false);
        var message = new QuoteFailedV1(Guid.NewGuid(), tripId, "NoDriverAvailable", DateTimeOffset.UtcNow, "corr");

        await using (var db = _fixture.CreateDbContext())
            await QuoteFailed(db).Consume(CreateConsumeContext(message));

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal("Failed", (await verify.Trips.FirstAsync(t => t.Id == tripId)).Status);
        Assert.Null(await verify.ActiveRiderTrips.FirstOrDefaultAsync(a => a.RiderId == riderId));
    }

    [LocalSqlServerFact]
    public async Task DriverMatched_OffersTripToDriver_AndClaimsDriver()
    {
        var tripId = await SeedTripAsync($"rider-{Guid.NewGuid():N}", confirmQuote: true);
        var driverId = $"drv-{Guid.NewGuid():N}";
        var message = new DriverMatchedV1(Guid.NewGuid(), tripId, driverId, 1.2, [], DateTimeOffset.UtcNow, "corr");

        await using (var db = _fixture.CreateDbContext())
            await DriverMatched(db).Consume(CreateConsumeContext(message));

        await using var verify = _fixture.CreateDbContext();
        var trip = await verify.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("DriverOffered", trip.Status);
        Assert.Equal(driverId, trip.OfferedDriverId);
        Assert.NotNull(await verify.ActiveDriverAssignments.FirstOrDefaultAsync(a => a.DriverId == driverId && a.TripId == tripId));
    }

    [LocalSqlServerFact]
    public async Task DriverMatched_WhenDriverBusy_RequestsAnotherMatchExcludingBusyDriver()
    {
        var tripId = await SeedTripAsync($"rider-{Guid.NewGuid():N}", confirmQuote: true);
        var otherTripId = await SeedTripAsync($"rider-{Guid.NewGuid():N}", confirmQuote: false);
        var busyDriver = $"drv-busy-{Guid.NewGuid():N}";
        await using (var seed = _fixture.CreateDbContext())
        {
            new ActiveAssignmentStore(seed).ClaimDriver(busyDriver, otherTripId);
            await new TripUnitOfWork(seed).SaveAsync(CancellationToken.None);
        }

        var message = new DriverMatchedV1(Guid.NewGuid(), tripId, busyDriver, 1.2, [], DateTimeOffset.UtcNow, "corr");
        await using (var db = _fixture.CreateDbContext())
            await DriverMatched(db).Consume(CreateConsumeContext(message));

        await using var verify = _fixture.CreateDbContext();
        var trip = await verify.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Null(trip.OfferedDriverId); // still waiting for a rematch
        var staged = await verify.IntegrationOutbox
            .Where(o => o.EventType == nameof(DriverMatchRequestedV1) && o.PayloadJson.Contains(tripId.ToString()))
            .ToListAsync();
        var request = Assert.Single(staged);
        Assert.Contains(busyDriver, request.PayloadJson);
        Assert.Equal(message.EventId, request.CausationId);
    }
}
