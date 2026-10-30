using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Payments;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Tests.Infrastructure;

[Collection("TripDatabase")]
public class TripPersistenceTests
{
    private readonly TripDatabaseFixture _fixture;

    public TripPersistenceTests(TripDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [LocalSqlServerFact]
    public async Task SaveAndLoad_PreservesFullSnapshotAndRoute()
    {
        await using var db = _fixture.CreateDbContext();
        var uow = new TripUnitOfWork(db);
        var writer = new TripRepository(db);
        var reader = new TripRepository(db);

        var tripId = Guid.NewGuid();
        var trip = Trip.Request(tripId, "rider-persist", 3);
        var quoteId = Guid.NewGuid();
        trip.ApplyQuote(new FareSnapshot(quoteId, 55000m, "VND"), "cand-1");
        trip.ConfirmQuote("rider-persist", quoteId, "driver-1");

        var now = DateTimeOffset.UtcNow;
        var context = new TripContext(
            trip,
            "Ben Thanh Market",
            "District 1, HCMC",
            10.7719,
            106.6983,
            "Tan Son Nhat Airport",
            "Tan Binh, HCMC",
            10.8185,
            106.6588,
            "Card",
            "card.success.v1",
            7.5m,
            25m,
            now,
            now);

        writer.Add(context);
        await uow.SaveAsync(CancellationToken.None);

        await using var readDb = _fixture.CreateDbContext();
        var loadedReader = new TripRepository(readDb);
        var loaded = await loadedReader.LoadAsync(tripId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(tripId, loaded.Trip.Id);
        Assert.Equal("rider-persist", loaded.Trip.RiderId);
        Assert.Equal(TripStatus.DriverOffered, loaded.Trip.Status);
        Assert.Equal("driver-1", loaded.Trip.OfferedDriverId);
        Assert.Equal(55000m, loaded.Trip.Fare?.Amount);
        Assert.Equal("Ben Thanh Market", loaded.PickupName);
        Assert.Equal(7.5m, loaded.DistanceKm);
        Assert.NotNull(loaded.RowVersion);
    }

    [LocalSqlServerFact]
    public async Task Concurrency_StaleRowVersion_ThrowsDbUpdateConcurrencyException()
    {
        await using var db1 = _fixture.CreateDbContext();
        var writer1 = new TripRepository(db1);
        var uow1 = new TripUnitOfWork(db1);

        var tripId = Guid.NewGuid();
        var trip = Trip.Request(tripId, "rider-conc", 3);
        var now = DateTimeOffset.UtcNow;
        var context = new TripContext(
            trip, "P1", "A1", 10, 106, "P2", "A2", 11, 107,
            "Card", "card.success.v1", 5m, 15m, now, now);

        writer1.Add(context);
        await uow1.SaveAsync(CancellationToken.None);

        // Load into db1
        var loaded1 = await writer1.LoadAsync(tripId, CancellationToken.None);
        Assert.NotNull(loaded1);

        // Load into db2
        await using var db2 = _fixture.CreateDbContext();
        var writer2 = new TripRepository(db2);
        var uow2 = new TripUnitOfWork(db2);
        var loaded2 = await writer2.LoadAsync(tripId, CancellationToken.None);
        Assert.NotNull(loaded2);

        // Update db1 first
        loaded1.Trip.Cancel("rider-conc");
        writer1.Update(loaded1);
        await uow1.SaveAsync(CancellationToken.None);

        // Update db2 with stale rowversion
        loaded2.Trip.Cancel("rider-conc");
        writer2.Update(loaded2);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => uow2.SaveAsync(CancellationToken.None));
    }

    [LocalSqlServerFact]
    public async Task ActiveRiderTrip_TwoTripsSameRider_ThrowsUniqueConstraintException()
    {
        await using var db = _fixture.CreateDbContext();
        var assignments = new ActiveAssignmentStore(db);
        var uow = new TripUnitOfWork(db);

        var riderId = "rider-single-active";
        assignments.ClaimRider(riderId, Guid.NewGuid());
        await uow.SaveAsync(CancellationToken.None);

        await using var db2 = _fixture.CreateDbContext();
        var assignments2 = new ActiveAssignmentStore(db2);
        var uow2 = new TripUnitOfWork(db2);

        assignments2.ClaimRider(riderId, Guid.NewGuid());
        await Assert.ThrowsAsync<DbUpdateException>(() => uow2.SaveAsync(CancellationToken.None));
    }

    [LocalSqlServerFact]
    public async Task ActiveDriverAssignment_TwoClaimsSameDriver_ThrowsUniqueConstraintException()
    {
        await using var db = _fixture.CreateDbContext();
        var assignments = new ActiveAssignmentStore(db);
        var uow = new TripUnitOfWork(db);

        var driverId = "driver-race-1";
        assignments.ClaimDriver(driverId, Guid.NewGuid());
        await uow.SaveAsync(CancellationToken.None);

        await using var db2 = _fixture.CreateDbContext();
        var assignments2 = new ActiveAssignmentStore(db2);
        var uow2 = new TripUnitOfWork(db2);

        assignments2.ClaimDriver(driverId, Guid.NewGuid());
        await Assert.ThrowsAsync<DbUpdateException>(() => uow2.SaveAsync(CancellationToken.None));
    }

    [LocalSqlServerFact]
    public async Task UnitOfWork_FailedSave_RollsBackReceiptAndAssignment()
    {
        var riderId = "rider-rollback-test";
        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Seed a conflicting active rider in another DbContext so the DB will throw a unique constraint violation on SaveChanges
        await using var seedDb = _fixture.CreateDbContext();
        var seedAssignments = new ActiveAssignmentStore(seedDb);
        var seedUow = new TripUnitOfWork(seedDb);
        seedAssignments.ClaimRider(riderId, Guid.NewGuid());
        await seedUow.SaveAsync(CancellationToken.None);

        await using var db = _fixture.CreateDbContext();
        var writer = new TripRepository(db);
        var assignments = new ActiveAssignmentStore(db);
        var receipts = new CommandReceiptStore(db);
        var uow = new TripUnitOfWork(db);

        var trip = Trip.Request(tripId, riderId, 3);
        var context = new TripContext(trip, "P1", "A1", 10, 106, "P2", "A2", 11, 107, "Card", "alias", 5m, 15m, now, now);

        writer.Add(context);
        receipts.Add(new CommandReceipt("scope-test", "key-1", "hash", null, "Pending", 1, null, now, now));
        assignments.ClaimRider(riderId, tripId); // Will conflict with existing rider in database

        await Assert.ThrowsAsync<DbUpdateException>(() => uow.SaveAsync(CancellationToken.None));

        // Verify that NEITHER trip nor receipt was persisted
        await using var verifyDb = _fixture.CreateDbContext();
        var verifyReader = new TripRepository(verifyDb);
        var verifyReceipts = new CommandReceiptStore(verifyDb);

        Assert.Null(await verifyReader.LoadAsync(tripId, CancellationToken.None));
        Assert.Null(await verifyReceipts.FindAsync(new CommandScope("rider-rollback-test", "rider", "test", tripId), "key-1", CancellationToken.None));
    }

    [LocalSqlServerFact]
    public async Task TripWorkStore_ClaimAndFence_OnlyOneOwnerProcesses()
    {
        await using var db = _fixture.CreateDbContext();
        var workStore = new TripWorkStore(db);
        var uow = new TripUnitOfWork(db);

        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");
        await uow.SaveAsync(CancellationToken.None);

        // Owner 1 claims
        var owner1 = Guid.NewGuid();
        var lease1 = await workStore.TryClaimAsync(TripWorkKind.Payment, owner1, now, CancellationToken.None);
        Assert.NotNull(lease1);
        Assert.Equal(owner1, lease1.Owner);

        // Owner 2 tries to claim while lease is active
        await using var db2 = _fixture.CreateDbContext();
        var workStore2 = new TripWorkStore(db2);
        var owner2 = Guid.NewGuid();
        var lease2 = await workStore2.TryClaimAsync(TripWorkKind.Payment, owner2, now.AddSeconds(10), CancellationToken.None);
        Assert.Null(lease2); // Cannot claim while lease1 is active

        // Owner 1 completes
        workStore.StageCompletion(lease1);
        await uow.SaveAsync(CancellationToken.None);

        // After completion, no one can claim
        await using var db3 = _fixture.CreateDbContext();
        var workStore3 = new TripWorkStore(db3);
        var lease3 = await workStore3.TryClaimAsync(TripWorkKind.Payment, owner2, now.AddMinutes(1), CancellationToken.None);
        Assert.Null(lease3);
    }

    [LocalSqlServerFact]
    public async Task TripQueryReader_FeedAndHistory_RespectsFiltersAndCardinality()
    {
        await using var db = _fixture.CreateDbContext();
        var writer = new TripRepository(db);
        var assignments = new ActiveAssignmentStore(db);
        var uow = new TripUnitOfWork(db);
        var query = new TripQueryReader(db);

        var riderId = "rider-history-test";
        var driverId = "driver-history-test";
        var now = DateTimeOffset.UtcNow;

        var trip1 = Trip.Request(Guid.NewGuid(), riderId, 3);
        trip1.ApplyQuote(new FareSnapshot(Guid.NewGuid(), 30000m, "VND"), "cand-1");
        trip1.ConfirmQuote(riderId, trip1.Fare!.QuoteId, driverId); // Offered to driverId

        var context1 = new TripContext(trip1, "P1", "A1", 10, 106, "D1", "A2", 11, 107, "Card", "alias", 3m, 10m, now.AddMinutes(-10), now.AddMinutes(-10));
        writer.Add(context1);

        var trip2 = Trip.Request(Guid.NewGuid(), riderId, 3);
        trip2.ApplyQuote(new FareSnapshot(Guid.NewGuid(), 60000m, "VND"), "cand-2");
        trip2.ConfirmQuote(riderId, trip2.Fare!.QuoteId, driverId);
        trip2.AcceptDriver(driverId); // Driver accepted -> Active trip for driver
        assignments.ClaimDriver(driverId, trip2.Id);

        var context2 = new TripContext(trip2, "P2", "A2", 10.1, 106.1, "D2", "A3", 11.1, 107.1, "Card", "alias", 6m, 20m, now.AddMinutes(-5), now.AddMinutes(-5));
        writer.Add(context2);

        await uow.SaveAsync(CancellationToken.None);

        // Driver feed should see context1 as Offered and context2 as Active
        var driverActor = new TripActor(driverId, "driver");
        var feed = await query.GetDriverFeedAsync(driverActor, CancellationToken.None);

        Assert.NotNull(feed.OfferedTrip);
        Assert.Equal(trip1.Id, feed.OfferedTrip.Trip.Id);
        Assert.NotNull(feed.ActiveTrip);
        Assert.Equal(trip2.Id, feed.ActiveTrip.Trip.Id);

        // Rider history should return both trips ordered by CreatedAt DESC
        var riderActor = new TripActor(riderId, "rider");
        var history = await query.GetHistoryAsync(riderActor, null, 10, CancellationToken.None);

        Assert.Equal(2, history.Items.Count);
        Assert.Equal(trip2.Id, history.Items[0].Trip.Id); // more recent
        Assert.Equal(trip1.Id, history.Items[1].Trip.Id);
    }

    [LocalSqlServerFact]
    public async Task TripWorkStore_StageRetry_OnFirstAttempt_DoesNotPrematurelyTriggerRequiresAction()
    {
        await using var db = _fixture.CreateDbContext();
        var workStore = new TripWorkStore(db);
        var uow = new TripUnitOfWork(db);

        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");
        await uow.SaveAsync(CancellationToken.None);

        var owner = Guid.NewGuid();
        var lease = await workStore.TryClaimAsync(TripWorkKind.Payment, owner, now, CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal(1, lease.AttemptCount);

        // Stage retry at attempt 1/20
        var nextAttempt = now.AddSeconds(10);
        workStore.StageRetry(lease, nextAttempt, "transient_error");
        await uow.SaveAsync(CancellationToken.None);

        await using var verifyDb = _fixture.CreateDbContext();
        var job = await verifyDb.TripPaymentJobs.FirstOrDefaultAsync(j => j.TripId == tripId);
        Assert.NotNull(job);
        Assert.False(job.RequiresAction, "Job must NOT have RequiresAction=true on first retry (1/20)");
        Assert.Null(job.Owner);
        Assert.Equal(nextAttempt, job.NextAttemptUtc);
    }

    [LocalSqlServerFact]
    public async Task TripWorkStore_StageCompletion_StaleOwner_DoesNotCompleteJobOfNewOwner()
    {
        await using var db = _fixture.CreateDbContext();
        var workStore = new TripWorkStore(db);
        var uow = new TripUnitOfWork(db);

        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");
        await uow.SaveAsync(CancellationToken.None);

        // Owner 1 claims lease
        var owner1 = Guid.NewGuid();
        var lease1 = await workStore.TryClaimAsync(TripWorkKind.Payment, owner1, now, CancellationToken.None);
        Assert.NotNull(lease1);

        // Time passes, lease expires, Owner 2 claims
        var futureNow = now.AddSeconds(35);
        var owner2 = Guid.NewGuid();
        var lease2 = await workStore.TryClaimAsync(TripWorkKind.Payment, owner2, futureNow, CancellationToken.None);
        Assert.NotNull(lease2);
        Assert.Equal(owner2, lease2.Owner);

        // Owner 1 wakes up late and attempts to complete lease1
        workStore.StageCompletion(lease1);
        await uow.SaveAsync(CancellationToken.None);

        // Verify in DB that job is still Owned by Owner2 and NOT marked Completed
        await using var verifyDb = _fixture.CreateDbContext();
        var job = await verifyDb.TripPaymentJobs.FirstOrDefaultAsync(j => j.TripId == tripId);
        Assert.NotNull(job);
        Assert.Equal(owner2, job.Owner);
        Assert.Equal("Pending", job.Status);
    }

    [LocalSqlServerFact]
    public async Task Transaction_FailedSaveOnDriverClaim_RollsBackBothDriverAssignmentAndTripStatus()
    {
        var tripId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverId = $"driver-atomic-{Guid.NewGuid():N}";
        var conflictRiderId = $"conflict-rider-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        // Seed initial Quoted trip and a conflicting active rider in DB
        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip = Trip.Request(tripId, riderId, 3);
            trip.ApplyQuote(new FareSnapshot(Guid.NewGuid(), 50000m, "VND"), "candidate");
            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "alias", 5m, 15m, now, now);
            writer.Add(ctx);

            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = conflictRiderId, TripId = Guid.NewGuid() });
            await uow.SaveAsync(CancellationToken.None);
        }

        // Now attempt atomic claim in a new context, but fail with an intentional constraint violation on SaveAsync
        await using (var failDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(failDb);
            var store = new ActiveAssignmentStore(failDb);
            var receipts = new CommandReceiptStore(failDb);
            var uow = new TripUnitOfWork(failDb);

            var loaded = await writer.LoadAsync(tripId, CancellationToken.None);
            Assert.NotNull(loaded);

            var claimed = store.TryClaimDriver(driverId, tripId);
            Assert.True(claimed);

            loaded.Trip.ConfirmQuote(riderId, loaded.Trip.Fare!.QuoteId, driverId);
            writer.Update(loaded);

            // Add duplicate conflictRiderId to force DB primary key violation on SaveAsync
            failDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = conflictRiderId, TripId = Guid.NewGuid() });

            await Assert.ThrowsAnyAsync<Exception>(() => uow.SaveAsync(CancellationToken.None));
        }

        // Verify that NEITHER the driver assignment NOR the trip status change were committed
        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var reader = new TripRepository(verifyDb);
            var trip = await reader.LoadAsync(tripId, CancellationToken.None);
            Assert.NotNull(trip);
            Assert.Equal(TripStatus.Quoted, trip.Trip.Status);
            Assert.Null(trip.Trip.OfferedDriverId);

            var driverAssigned = await verifyDb.ActiveDriverAssignments.AnyAsync(a => a.DriverId == driverId || a.TripId == tripId);
            Assert.False(driverAssigned, "Driver slot must NOT be held if the transaction failed");
        }
    }

    [LocalSqlServerFact]
    public async Task Transaction_FailedSaveOnPaymentSettlement_RollsBackBothJobCompletionAndPaidStatus()
    {
        var tripId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverId = $"driver-settle-{Guid.NewGuid():N}";
        var conflictRiderId = $"conflict-settle-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        // Seed trip at PaymentPending, job at Pending, and a conflicting active rider in DB
        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var workStore = new TripWorkStore(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip = Trip.Request(tripId, riderId, 3);
            trip.ApplyQuote(new FareSnapshot(Guid.NewGuid(), 50000m, "VND"), driverId);
            trip.ConfirmQuote(riderId, trip.Fare!.QuoteId, driverId);
            trip.AcceptDriver(driverId);
            trip.StartEnRoute(driverId);
            trip.Pickup(driverId);
            trip.Dropoff(driverId, now);
            trip.RequestPayment();

            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "alias", 5m, 15m, now, now);
            writer.Add(ctx);
            workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");

            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = conflictRiderId, TripId = Guid.NewGuid() });
            await uow.SaveAsync(CancellationToken.None);
        }

        // Lease the job
        var owner = Guid.NewGuid();
        TripWorkLease? lease;
        await using (var leaseDb = _fixture.CreateDbContext())
        {
            var workStore = new TripWorkStore(leaseDb);
            lease = await workStore.TryClaimAsync(TripWorkKind.Payment, owner, now, CancellationToken.None);
            Assert.NotNull(lease);
        }

        // Settle payment in transaction but fail with constraint error
        await using (var failDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(failDb);
            var workStore = new TripWorkStore(failDb);
            var uow = new TripUnitOfWork(failDb);

            var loaded = await writer.LoadAsync(tripId, CancellationToken.None);
            Assert.NotNull(loaded);

            var settlement = new PaymentSettlement(Guid.NewGuid(), tripId, 50000m, "VND", $"trip-{tripId:D}-payment");
            loaded.Trip.SettlePayment(settlement);
            writer.Update(loaded);

            workStore.StageCompletion(lease);

            // Force fail transaction with duplicate conflictRiderId
            failDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = conflictRiderId, TripId = Guid.NewGuid() });

            await Assert.ThrowsAnyAsync<Exception>(() => uow.SaveAsync(CancellationToken.None));
        }

        // Verify that NEITHER the job completion NOR the trip Paid status was committed
        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var reader = new TripRepository(verifyDb);
            var trip = await reader.LoadAsync(tripId, CancellationToken.None);
            Assert.NotNull(trip);
            Assert.Equal(TripStatus.PaymentPending, trip.Trip.Status);

            var job = await verifyDb.TripPaymentJobs.FirstOrDefaultAsync(j => j.TripId == tripId);
            Assert.NotNull(job);
            Assert.Equal("Pending", job.Status);
            Assert.Equal(owner, job.Owner);
        }
    }

    [LocalSqlServerFact]
    public async Task DriverReject_RematchesFreeDriver_AtomicTransaction_TransfersDriverSlotAndIncrementsCount()
    {
        var tripId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverA = $"driver-rej-a-{Guid.NewGuid():N}";
        var driverB = $"driver-rej-b-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        // 1. Seed trip in DriverOffered state with driverA and active driver assignment in DB
        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip = Trip.Request(tripId, riderId, 3);
            trip.ApplyQuote(new FareSnapshot(quoteId, 50000m, "VND"), driverA);
            trip.ConfirmQuote(riderId, quoteId, driverA);

            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "alias", 5m, 15m, now, now);
            writer.Add(ctx);

            seedDb.ActiveDriverAssignments.Add(new ActiveDriverAssignmentRecord { DriverId = driverA, TripId = tripId });
            await uow.SaveAsync(CancellationToken.None);
        }

        // 2. Setup mock matching to return driverB (free) on rematch
        var matchingMock = new Moq.Mock<IMatchingClient>();
        matchingMock.Setup(m => m.MatchAsync(Moq.It.IsAny<MatchInput>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver(driverB, 1.5)));

        // 3. Driver A rejects the offer
        await using (var db = _fixture.CreateDbContext())
        {
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

            var outcome = await sut.ExecuteAsync(
                new TripActor(driverA, "driver"),
                tripId,
                new DriverRejectCommand(),
                $"cmd-reject-{tripId:N}",
                CancellationToken.None);

            Assert.Equal("DriverOffered", outcome.Trip.Status);
            Assert.Equal(driverB, outcome.Trip.OfferedDriverId);
            Assert.Equal(1, outcome.Trip.RejectionCount);
        }

        // 4. Verify in DB: slot A deleted, slot B assigned to tripId in the same transaction
        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var slotA = await verifyDb.ActiveDriverAssignments.FirstOrDefaultAsync(a => a.DriverId == driverA);
            var slotB = await verifyDb.ActiveDriverAssignments.FirstOrDefaultAsync(a => a.DriverId == driverB);

            Assert.Null(slotA);
            Assert.NotNull(slotB);
            Assert.Equal(tripId, slotB.TripId);

            var trip = await verifyDb.Trips.FirstOrDefaultAsync(t => t.Id == tripId);
            Assert.NotNull(trip);
            Assert.Equal("DriverOffered", trip.Status);
            Assert.Equal(driverB, trip.OfferedDriverId);
            Assert.Equal(1, trip.RejectionCount);
        }
    }

    [LocalSqlServerFact]
    public async Task DriverReject_FailedTransaction_RollsBackBothDriverAssignmentAndTripStatus()
    {
        var tripId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverA = $"driver-rej-a-{Guid.NewGuid():N}";
        var driverB = $"driver-rej-b-{Guid.NewGuid():N}";
        var conflictRiderId = $"conflict-rider-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip = Trip.Request(tripId, riderId, 3);
            trip.ApplyQuote(new FareSnapshot(quoteId, 50000m, "VND"), driverA);
            trip.ConfirmQuote(riderId, quoteId, driverA);

            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "alias", 5m, 15m, now, now);
            writer.Add(ctx);

            seedDb.ActiveDriverAssignments.Add(new ActiveDriverAssignmentRecord { DriverId = driverA, TripId = tripId });
            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = conflictRiderId, TripId = Guid.NewGuid() });
            await uow.SaveAsync(CancellationToken.None);
        }

        var matchingMock = new Moq.Mock<IMatchingClient>();
        matchingMock.Setup(m => m.MatchAsync(Moq.It.IsAny<MatchInput>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver(driverB, 1.5)));

        await using (var failDb = _fixture.CreateDbContext())
        {
            var store = new ActiveAssignmentStore(failDb);
            var writer = new TripRepository(failDb);
            var uow = new TripUnitOfWork(failDb);

            var loaded = await writer.LoadAsync(tripId, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.Trip.RejectDriver(driverA);
            store.ReleaseDriver(tripId);

            var claimed = store.TryClaimDriver(driverB, tripId);
            Assert.True(claimed);
            loaded.Trip.OfferReplacementDriver(driverB);
            writer.Update(loaded);

            // Inject duplicate key conflict into the same transaction
            failDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = conflictRiderId, TripId = Guid.NewGuid() });

            await Assert.ThrowsAnyAsync<Exception>(() => uow.SaveAsync(CancellationToken.None));
        }

        // Verify that Driver A is still held and Trip is still offered to Driver A (rollback preserved original state)
        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var slotA = await verifyDb.ActiveDriverAssignments.FirstOrDefaultAsync(a => a.DriverId == driverA);
            var slotB = await verifyDb.ActiveDriverAssignments.FirstOrDefaultAsync(a => a.DriverId == driverB);

            Assert.NotNull(slotA);
            Assert.Equal(tripId, slotA.TripId);
            Assert.Null(slotB);

            var trip = await verifyDb.Trips.FirstOrDefaultAsync(t => t.Id == tripId);
            Assert.NotNull(trip);
            Assert.Equal("DriverOffered", trip.Status);
            Assert.Equal(driverA, trip.OfferedDriverId);
            Assert.Equal(0, trip.RejectionCount);
        }
    }

    [LocalSqlServerFact]
    public async Task TripPaymentJob_FencingInterleaving_StageCompletionA_ClaimB_SaveA_ThrowsDbUpdateConcurrencyException()
    {
        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = _fixture.CreateDbContext())
        {
            await seed.TripPaymentJobs.ExecuteDeleteAsync();
            new TripWorkStore(seed).AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");
            await seed.SaveChangesAsync();
        }

        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        await using var contextA = _fixture.CreateDbContext();
        await using var contextB = _fixture.CreateDbContext();

        var storeA = new TripWorkStore(contextA);
        var storeB = new TripWorkStore(contextB);

        var leaseA = await storeA.TryClaimAsync(TripWorkKind.Payment, ownerA, now, CancellationToken.None);
        Assert.NotNull(leaseA);
        Assert.Equal(tripId, leaseA.TripId);

        // A stages completion
        storeA.StageCompletion(leaseA);

        // Meanwhile, A's lease expires at now+31s, and B claims the job
        var leaseB = await storeB.TryClaimAsync(TripWorkKind.Payment, ownerB, now.AddSeconds(31), CancellationToken.None);
        Assert.NotNull(leaseB);
        Assert.Equal(tripId, leaseB.TripId);

        // A attempts to save its stale completion
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextA.SaveChangesAsync());

        // Verify in DB that job is still Owned by B and NOT Completed
        await using var verifyDb = _fixture.CreateDbContext();
        var job = await verifyDb.TripPaymentJobs.SingleAsync(j => j.TripId == tripId);
        Assert.Equal("Pending", job.Status);
        Assert.Equal(ownerB, job.Owner);

        await verifyDb.TripPaymentJobs.Where(j => j.TripId == tripId).ExecuteDeleteAsync();
    }

    [LocalSqlServerFact]
    public async Task TripPaymentJob_FencingInterleaving_StageRetryA_ClaimB_SaveA_ThrowsDbUpdateConcurrencyException()
    {
        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = _fixture.CreateDbContext())
        {
            await seed.TripPaymentJobs.ExecuteDeleteAsync();
            new TripWorkStore(seed).AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");
            await seed.SaveChangesAsync();
        }

        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        await using var contextA = _fixture.CreateDbContext();
        await using var contextB = _fixture.CreateDbContext();

        var storeA = new TripWorkStore(contextA);
        var storeB = new TripWorkStore(contextB);

        var leaseA = await storeA.TryClaimAsync(TripWorkKind.Payment, ownerA, now, CancellationToken.None);
        Assert.NotNull(leaseA);
        Assert.Equal(tripId, leaseA.TripId);

        // A stages retry
        storeA.StageRetry(leaseA, now.AddSeconds(60), "transient_error");

        // Meanwhile, A's lease expires at now+31s, and B claims the job
        var leaseB = await storeB.TryClaimAsync(TripWorkKind.Payment, ownerB, now.AddSeconds(31), CancellationToken.None);
        Assert.NotNull(leaseB);
        Assert.Equal(tripId, leaseB.TripId);

        // A attempts to save its stale retry
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextA.SaveChangesAsync());

        // Verify in DB that job is still Owned by B and in Pending status
        await using var verifyDb = _fixture.CreateDbContext();
        var job = await verifyDb.TripPaymentJobs.SingleAsync(j => j.TripId == tripId);
        Assert.Equal("Pending", job.Status);
        Assert.Equal(ownerB, job.Owner);

        await verifyDb.TripPaymentJobs.Where(j => j.TripId == tripId).ExecuteDeleteAsync();
    }

    [LocalSqlServerFact]
    public async Task TripPaymentJob_FencingInterleaving_StageRequiresActionA_ClaimB_SaveA_ThrowsDbUpdateConcurrencyException()
    {
        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = _fixture.CreateDbContext())
        {
            await seed.TripPaymentJobs.ExecuteDeleteAsync();
            new TripWorkStore(seed).AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");
            await seed.SaveChangesAsync();
        }

        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        await using var contextA = _fixture.CreateDbContext();
        await using var contextB = _fixture.CreateDbContext();

        var storeA = new TripWorkStore(contextA);
        var storeB = new TripWorkStore(contextB);

        var leaseA = await storeA.TryClaimAsync(TripWorkKind.Payment, ownerA, now, CancellationToken.None);
        Assert.NotNull(leaseA);
        Assert.Equal(tripId, leaseA.TripId);

        // A stages requires action
        storeA.StageRequiresAction(leaseA, "permanent_failure");

        // Meanwhile, A's lease expires at now+31s, and B claims the job
        var leaseB = await storeB.TryClaimAsync(TripWorkKind.Payment, ownerB, now.AddSeconds(31), CancellationToken.None);
        Assert.NotNull(leaseB);
        Assert.Equal(tripId, leaseB.TripId);

        // A attempts to save its stale requires action
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextA.SaveChangesAsync());

        // Verify in DB that job is still Owned by B and in Pending status
        await using var verifyDb = _fixture.CreateDbContext();
        var job = await verifyDb.TripPaymentJobs.SingleAsync(j => j.TripId == tripId);
        Assert.Equal("Pending", job.Status);
        Assert.Equal(ownerB, job.Owner);

        await verifyDb.TripPaymentJobs.Where(j => j.TripId == tripId).ExecuteDeleteAsync();
    }

    [LocalSqlServerFact]
    public async Task InitialPayment_Settled_OnUnownedJob_SuccessfullySettlesAndCompletesJobInSameTransaction()
    {
        var tripId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverId = $"driver-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var paymentId = Guid.NewGuid();

        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var workStore = new TripWorkStore(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip = Trip.Request(tripId, riderId, 3);
            trip.ApplyQuote(new FareSnapshot(quoteId, 50000m, "VND"), driverId);
            trip.ConfirmQuote(riderId, quoteId, driverId);
            trip.AcceptDriver(driverId);
            trip.StartEnRoute(driverId);
            trip.Pickup(driverId);
            trip.Dropoff(driverId, now);
            trip.RequestPayment();

            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok_card", 5m, 15m, now, now);
            writer.Add(ctx);

            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = riderId, TripId = tripId });
            workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");

            await uow.SaveAsync(CancellationToken.None);
        }

        var paymentClientMock = new Mock<IPaymentClient>();
        paymentClientMock.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(paymentId, tripId, riderId, driverId, 50000m, "VND", RemotePaymentStatus.Settled, null, false, null, null));

        await using (var runDb = _fixture.CreateDbContext())
        {
            var reader = new TripRepository(runDb);
            var writer = new TripRepository(runDb);
            var uow = new TripUnitOfWork(runDb);
            var store = new ActiveAssignmentStore(runDb);
            var workStore = new TripWorkStore(runDb);

            var coordinator = new TripPaymentCoordinator(
                reader, writer, uow, store, workStore,
                paymentClientMock.Object,
                Options.Create(new PaymentWorkflowOptions()),
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TripPaymentCoordinator>.Instance);

            var loaded = await reader.LoadAsync(tripId, CancellationToken.None);
            Assert.NotNull(loaded);

            var success = await coordinator.ExecuteInitialPaymentAsync(loaded, CancellationToken.None);
            Assert.True(success);
        }

        // Verify in DB that trip is Paid, rider slot released, and unowned job is marked Completed without concurrency exception
        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var trip = await verifyDb.Trips.SingleAsync(t => t.Id == tripId);
            Assert.Equal("Paid", trip.Status);
            Assert.Equal(paymentId, trip.PaymentId);

            var riderSlot = await verifyDb.ActiveRiderTrips.FirstOrDefaultAsync(r => r.TripId == tripId);
            Assert.Null(riderSlot);

            var job = await verifyDb.TripPaymentJobs.SingleAsync(j => j.TripId == tripId);
            Assert.Equal("Completed", job.Status);
            Assert.Null(job.Owner);

            await verifyDb.TripPaymentJobs.Where(j => j.TripId == tripId).ExecuteDeleteAsync();
        }
    }

    [LocalSqlServerFact]
    public async Task InitialPayment_Pending_OnUnownedJob_UpdatesJobNextAttemptWithoutConcurrencyException()
    {
        var tripId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverId = $"driver-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var paymentId = Guid.NewGuid();

        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var workStore = new TripWorkStore(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip = Trip.Request(tripId, riderId, 3);
            trip.ApplyQuote(new FareSnapshot(quoteId, 50000m, "VND"), driverId);
            trip.ConfirmQuote(riderId, quoteId, driverId);
            trip.AcceptDriver(driverId);
            trip.StartEnRoute(driverId);
            trip.Pickup(driverId);
            trip.Dropoff(driverId, now);
            trip.RequestPayment();

            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok_card", 5m, 15m, now, now);
            writer.Add(ctx);

            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = riderId, TripId = tripId });
            workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");

            await uow.SaveAsync(CancellationToken.None);
        }

        var paymentClientMock = new Mock<IPaymentClient>();
        paymentClientMock.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(paymentId, tripId, riderId, driverId, 50000m, "VND", RemotePaymentStatus.Pending, null, false, null, null));

        await using (var runDb = _fixture.CreateDbContext())
        {
            var reader = new TripRepository(runDb);
            var writer = new TripRepository(runDb);
            var uow = new TripUnitOfWork(runDb);
            var store = new ActiveAssignmentStore(runDb);
            var workStore = new TripWorkStore(runDb);

            var coordinator = new TripPaymentCoordinator(
                reader, writer, uow, store, workStore,
                paymentClientMock.Object,
                Options.Create(new PaymentWorkflowOptions()),
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TripPaymentCoordinator>.Instance);

            var loaded = await reader.LoadAsync(tripId, CancellationToken.None);
            Assert.NotNull(loaded);

            var success = await coordinator.ExecuteInitialPaymentAsync(loaded, CancellationToken.None);
            Assert.False(success);
        }

        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var trip = await verifyDb.Trips.SingleAsync(t => t.Id == tripId);
            Assert.Equal("PaymentPending", trip.Status);

            var job = await verifyDb.TripPaymentJobs.SingleAsync(j => j.TripId == tripId);
            Assert.Equal("Pending", job.Status);
            Assert.Equal(1, job.Attempts);
            Assert.Null(job.Owner);

            await verifyDb.TripPaymentJobs.Where(j => j.TripId == tripId).ExecuteDeleteAsync();
        }
    }

    [LocalSqlServerFact]
    public async Task InitialPayment_Failed_OnUnownedJob_RecordsFailureAndSetsRequiresAction()
    {
        var tripId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverId = $"driver-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var paymentId = Guid.NewGuid();

        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var workStore = new TripWorkStore(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip = Trip.Request(tripId, riderId, 3);
            trip.ApplyQuote(new FareSnapshot(quoteId, 50000m, "VND"), driverId);
            trip.ConfirmQuote(riderId, quoteId, driverId);
            trip.AcceptDriver(driverId);
            trip.StartEnRoute(driverId);
            trip.Pickup(driverId);
            trip.Dropoff(driverId, now);
            trip.RequestPayment();

            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok_card", 5m, 15m, now, now);
            writer.Add(ctx);

            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = riderId, TripId = tripId });
            workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");

            await uow.SaveAsync(CancellationToken.None);
        }

        var paymentClientMock = new Mock<IPaymentClient>();
        paymentClientMock.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(paymentId, tripId, riderId, driverId, 50000m, "VND", RemotePaymentStatus.Failed, "Permanent", false, null, null));

        await using (var runDb = _fixture.CreateDbContext())
        {
            var reader = new TripRepository(runDb);
            var writer = new TripRepository(runDb);
            var uow = new TripUnitOfWork(runDb);
            var store = new ActiveAssignmentStore(runDb);
            var workStore = new TripWorkStore(runDb);

            var coordinator = new TripPaymentCoordinator(
                reader, writer, uow, store, workStore,
                paymentClientMock.Object,
                Options.Create(new PaymentWorkflowOptions()),
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TripPaymentCoordinator>.Instance);

            var loaded = await reader.LoadAsync(tripId, CancellationToken.None);
            Assert.NotNull(loaded);

            var success = await coordinator.ExecuteInitialPaymentAsync(loaded, CancellationToken.None);
            Assert.False(success);
        }

        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var trip = await verifyDb.Trips.SingleAsync(t => t.Id == tripId);
            Assert.Equal("PaymentPending", trip.Status);
            Assert.Equal("Permanent", trip.LastPaymentFailure);

            var job = await verifyDb.TripPaymentJobs.SingleAsync(j => j.TripId == tripId);
            Assert.True(job.RequiresAction);
            Assert.Null(job.Owner);

            await verifyDb.TripPaymentJobs.Where(j => j.TripId == tripId).ExecuteDeleteAsync();
        }
    }

    [LocalSqlServerFact]
    public async Task InitialPayment_Settled_RaceWithWorkerClaim_FailsWithConcurrencyExceptionAndDoesNotOverwriteWorker()
    {
        var tripId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverId = $"driver-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var paymentId = Guid.NewGuid();

        await using (var seedDb = _fixture.CreateDbContext())
        {
            var writer = new TripRepository(seedDb);
            var workStore = new TripWorkStore(seedDb);
            var uow = new TripUnitOfWork(seedDb);

            var trip = Trip.Request(tripId, riderId, 3);
            trip.ApplyQuote(new FareSnapshot(quoteId, 50000m, "VND"), driverId);
            trip.ConfirmQuote(riderId, quoteId, driverId);
            trip.AcceptDriver(driverId);
            trip.StartEnRoute(driverId);
            trip.Pickup(driverId);
            trip.Dropoff(driverId, now);
            trip.RequestPayment();

            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok_card", 5m, 15m, now, now);
            writer.Add(ctx);

            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = riderId, TripId = tripId });
            workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 20, now.AddMinutes(10), "Rest");

            await uow.SaveAsync(CancellationToken.None);
        }

        // Background worker B claims the job first
        var workerB = Guid.NewGuid();
        await using (var workerDb = _fixture.CreateDbContext())
        {
            var workStore = new TripWorkStore(workerDb);
            var leaseB = await workStore.TryClaimAsync(TripWorkKind.Payment, workerB, now, CancellationToken.None);
            Assert.NotNull(leaseB);
            Assert.Equal(tripId, leaseB.TripId);
        }

        // Now HTTP initial call tries to settle unowned (it thinks Owner is NULL)
        await using (var httpDb = _fixture.CreateDbContext())
        {
            var workStore = new TripWorkStore(httpDb);
            var uow = new TripUnitOfWork(httpDb);

            workStore.StageUnownedCompletion(tripId);

            // Save must throw DbUpdateConcurrencyException because Owner in DB is workerB, not NULL
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => uow.SaveAsync(CancellationToken.None));
        }

        // Verify that Worker B's lease is still intact on DB
        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var job = await verifyDb.TripPaymentJobs.SingleAsync(j => j.TripId == tripId);
            Assert.Equal("Pending", job.Status);
            Assert.Equal(workerB, job.Owner);

            await verifyDb.TripPaymentJobs.Where(j => j.TripId == tripId).ExecuteDeleteAsync();
        }
    }

    [LocalSqlServerFact]
    public async Task TripWorkStore_StageRequiresAction_MatchingLease_DoesNotDirectlyMutateTripOrActiveRider()
    {
        var tripId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = _fixture.CreateDbContext())
        {
            var trip = Trip.Request(tripId, riderId, 3);
            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok_card", null, null, now, now);
            new TripRepository(seedDb).Add(ctx);

            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = riderId, TripId = tripId });
            seedDb.CommandReceipts.Add(new CommandReceiptRecord
            {
                Scope = "CreateTrip",
                Key = $"create-trip-{tripId:D}",
                RequestHash = "hash",
                Status = "pending",
                ResponseJson = tripId.ToString("D"),
                CreatedAt = now,
                UpdatedAt = now
            });

            await seedDb.SaveChangesAsync();
        }

        await using (var runDb = _fixture.CreateDbContext())
        {
            var workStore = new TripWorkStore(runDb);
            var lease = new TripWorkLease(TripWorkKind.Matching, Guid.NewGuid(), tripId, Guid.NewGuid(), now.AddMinutes(1), 5);

            workStore.StageRequiresAction(lease, "matching_attempts_exhausted");
            await runDb.SaveChangesAsync();
        }

        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var trip = await verifyDb.Trips.SingleAsync(t => t.Id == tripId);
            Assert.Equal("Requested", trip.Status);

            var riderSlot = await verifyDb.ActiveRiderTrips.FirstOrDefaultAsync(r => r.TripId == tripId);
            Assert.NotNull(riderSlot);

            var receipt = await verifyDb.CommandReceipts.SingleAsync(r => r.ResponseJson == tripId.ToString("D"));
            Assert.Equal("failed", receipt.Status);

            await verifyDb.Trips.Where(t => t.Id == tripId).ExecuteDeleteAsync();
            await verifyDb.ActiveRiderTrips.Where(r => r.TripId == tripId).ExecuteDeleteAsync();
            await verifyDb.CommandReceipts.Where(r => r.ResponseJson == tripId.ToString("D")).ExecuteDeleteAsync();
        }
    }

    [LocalSqlServerFact]
    public async Task TripWorkCoordinator_FailMatchingExhausted_AtomicallyTransitionsTripToFailedAndReleasesRider()
    {
        var tripId = Guid.NewGuid();
        var riderId = $"rider-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = _fixture.CreateDbContext())
        {
            var trip = Trip.Request(tripId, riderId, 3);
            var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok_card", null, null, now, now);
            new TripRepository(seedDb).Add(ctx);

            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = riderId, TripId = tripId });
            seedDb.CommandReceipts.Add(new CommandReceiptRecord
            {
                Scope = "CreateTrip",
                Key = $"create-trip-{tripId:D}",
                RequestHash = "hash",
                Status = "pending",
                ResponseJson = tripId.ToString("D"),
                CreatedAt = now,
                UpdatedAt = now
            });

            await seedDb.SaveChangesAsync();
        }

        await using (var runDb = _fixture.CreateDbContext())
        {
            var reader = new TripRepository(runDb);
            var writer = new TripRepository(runDb);
            var uow = new TripUnitOfWork(runDb);
            var store = new ActiveAssignmentStore(runDb);
            var workStore = new TripWorkStore(runDb);
            var receiptStore = new CommandReceiptStore(runDb);
            var matchingClientMock = new Mock<IMatchingClient>();

            var coordinator = new TripWorkCoordinator(
                reader, writer, uow, receiptStore, store,
                matchingClientMock.Object,
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TripWorkCoordinator>.Instance);

            var lease = new TripWorkLease(TripWorkKind.Matching, Guid.NewGuid(), tripId, Guid.NewGuid(), now.AddMinutes(1), 5);

            await coordinator.FailMatchingExhaustedAsync(lease, CancellationToken.None);
            workStore.StageRequiresAction(lease, "matching_attempts_exhausted");
            await uow.SaveAsync(CancellationToken.None);
        }

        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var trip = await verifyDb.Trips.SingleAsync(t => t.Id == tripId);
            Assert.Equal("Failed", trip.Status);

            var riderSlot = await verifyDb.ActiveRiderTrips.FirstOrDefaultAsync(r => r.TripId == tripId);
            Assert.Null(riderSlot);

            var receipt = await verifyDb.CommandReceipts.SingleAsync(r => r.ResponseJson == tripId.ToString("D"));
            Assert.Equal("failed", receipt.Status);

            await verifyDb.Trips.Where(t => t.Id == tripId).ExecuteDeleteAsync();
            await verifyDb.ActiveRiderTrips.Where(r => r.TripId == tripId).ExecuteDeleteAsync();
            await verifyDb.CommandReceipts.Where(r => r.ResponseJson == tripId.ToString("D")).ExecuteDeleteAsync();
        }
    }
}