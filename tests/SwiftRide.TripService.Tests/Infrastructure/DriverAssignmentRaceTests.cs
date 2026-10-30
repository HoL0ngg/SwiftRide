using Microsoft.EntityFrameworkCore;
using Moq;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Persistence;
using Xunit;

namespace SwiftRide.TripService.Tests.Infrastructure;

[Collection("TripDatabase")]
public class DriverAssignmentRaceTests
{
    private readonly TripDatabaseFixture _fixture;

    public DriverAssignmentRaceTests(TripDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [LocalSqlServerFact]
    public async Task TwoTrips_ClaimingSameDriverConcurrently_ExactlyOneWins_OtherFailsWithUniqueConstraint()
    {
        var driverId = $"driver-race-{Guid.NewGuid():N}";
        var trip1Id = Guid.NewGuid();
        var trip2Id = Guid.NewGuid();

        await using var db1 = _fixture.CreateDbContext();
        await using var db2 = _fixture.CreateDbContext();

        var store1 = new ActiveAssignmentStore(db1);
        var store2 = new ActiveAssignmentStore(db2);

        var writer1 = new TripRepository(db1);
        var writer2 = new TripRepository(db2);

        var trip1 = Trip.Request(trip1Id, $"rider-1-{Guid.NewGuid():N}", 3);
        var trip2 = Trip.Request(trip2Id, $"rider-2-{Guid.NewGuid():N}", 3);

        var now = DateTimeOffset.UtcNow;
        var ctx1 = new TripContext(trip1, "P1", "A1", 10.7, 106.6, "D1", "A1", 10.8, 106.7, "card", "alias", 5m, 15m, now, now);
        var ctx2 = new TripContext(trip2, "P2", "A2", 10.7, 106.6, "D2", "A2", 10.8, 106.7, "card", "alias", 5m, 15m, now, now);

        writer1.Add(ctx1);
        writer2.Add(ctx2);

        store1.ClaimDriver(driverId, trip1Id);
        store2.ClaimDriver(driverId, trip2Id);

        var save1Task = db1.SaveChangesAsync();
        var save2Task = db2.SaveChangesAsync();

        var results = await Task.WhenAll(
            save1Task.ContinueWith(t => t.Exception is null ? "Success" : "Failed"),
            save2Task.ContinueWith(t => t.Exception is null ? "Success" : "Failed"));

        var successes = results.Count(r => r == "Success");
        var failures = results.Count(r => r == "Failed");

        Assert.Equal(1, successes);
        Assert.Equal(1, failures);
    }

    [LocalSqlServerFact]
    public async Task TwoTrips_ConfirmQuoteSameDriverConcurrently_AtomicTransactionResolvesRaceWithRematch()
    {
        var driver1Id = $"driver-race-1-{Guid.NewGuid():N}";
        var driver2Id = $"driver-race-2-{Guid.NewGuid():N}";
        var trip1Id = Guid.NewGuid();
        var trip2Id = Guid.NewGuid();
        var quote1Id = Guid.NewGuid();
        var quote2Id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Seed 2 trips at Quoted
        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip1 = Trip.Request(trip1Id, "rider-race-1", 3);
            trip1.ApplyQuote(new FareSnapshot(quote1Id, 50000m, "VND"), driver1Id);
            var ctx1 = new TripContext(trip1, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "alias", 5m, 15m, now, now);

            var trip2 = Trip.Request(trip2Id, "rider-race-2", 3);
            trip2.ApplyQuote(new FareSnapshot(quote2Id, 50000m, "VND"), driver1Id);
            var ctx2 = new TripContext(trip2, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "alias", 5m, 15m, now, now);

            writer.Add(ctx1);
            writer.Add(ctx2);
            await uow.SaveAsync(CancellationToken.None);
        }

        // Run ConfirmQuote concurrently where matching service returns driver1Id first, then driver2Id for rematch
        var matchingMock = new Moq.Mock<IMatchingClient>();
        matchingMock.Setup(m => m.MatchAsync(Moq.It.Is<MatchInput>(i => !i.ExcludedDriverIds.Contains(driver1Id)), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver(driver1Id, 1.0)));
        matchingMock.Setup(m => m.MatchAsync(Moq.It.Is<MatchInput>(i => i.ExcludedDriverIds.Contains(driver1Id)), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver(driver2Id, 2.0)));

        async Task<TripCommandOutcome> RunConfirm(Guid tripId, Guid quoteId, string riderId)
        {
            await using var db = _fixture.CreateDbContext();
            var reader = new TripRepository(db);
            var writer = new TripRepository(db);
            var uow = new TripUnitOfWork(db);
            var receipts = new CommandReceiptStore(db);
            var store = new ActiveAssignmentStore(db);
            var workStore = new TripWorkStore(db);
            var sut = new TripCommandUseCase(
                reader, writer, uow, receipts, store, workStore,
                matchingMock.Object, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TripCommandUseCase>.Instance);

            return await sut.ExecuteAsync(
                new TripActor(riderId, "rider"),
                tripId,
                new ConfirmQuoteCommand(quoteId),
                $"key-{tripId:N}",
                CancellationToken.None);
        }

        var task1 = Task.Run(() => RunConfirm(trip1Id, quote1Id, "rider-race-1"));
        var task2 = Task.Run(() => RunConfirm(trip2Id, quote2Id, "rider-race-2"));

        var outcomes = await Task.WhenAll(task1, task2);

        // Both trips must succeed and be in DriverOffered state
        Assert.Equal("DriverOffered", outcomes[0].Trip.Status);
        Assert.Equal("DriverOffered", outcomes[1].Trip.Status);

        // Exactly one got driver1, the other got driver2!
        var offeredDrivers = new HashSet<string?> { outcomes[0].Trip.OfferedDriverId, outcomes[1].Trip.OfferedDriverId };
        Assert.Contains(driver1Id, offeredDrivers);
        Assert.Contains(driver2Id, offeredDrivers);
    }
}
