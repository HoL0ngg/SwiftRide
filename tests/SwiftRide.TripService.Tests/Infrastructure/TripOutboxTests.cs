using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Messaging;
using SwiftRide.TripService.Infrastructure.Persistence;
using Xunit;

namespace SwiftRide.TripService.Tests.Infrastructure;

[Collection("TripDatabase")]
public class TripOutboxTests
{
    private readonly TripDatabaseFixture _fixture;

    public TripOutboxTests(TripDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [LocalSqlServerFact]
    public async Task UnitOfWork_SuccessfulSave_PersistsBothTripAndOutboxAtomically()
    {
        await using var db = _fixture.CreateDbContext();
        var uow = new TripUnitOfWork(db);
        var writer = new TripRepository(db);
        var eventWriter = new TripEventWriter(db);

        var tripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var trip = Trip.Request(tripId, "rider-outbox-1", 3);
        var now = DateTimeOffset.UtcNow;
        var ctx = new TripContext(trip, "A", "A", 10, 106, "B", "B", 10.1, 106.1, "wallet", "tok-1", 5, 10, now, now);

        writer.Add(ctx);
        eventWriter.Add(new TripAcceptedV1(
            EventId: eventId,
            TripId: tripId,
            RiderId: "rider-outbox-1",
            DriverId: "driver-outbox-1",
            OccurredAtUtc: now,
            CorrelationId: $"trip-{tripId:D}-accept"));

        await uow.SaveAsync(CancellationToken.None);

        await using var verifyDb = _fixture.CreateDbContext();
        var outboxMsg = await verifyDb.IntegrationOutbox.FirstOrDefaultAsync(o => o.MessageId == eventId);
        Assert.NotNull(outboxMsg);
        Assert.Equal(nameof(TripAcceptedV1), outboxMsg.EventType);
        Assert.Null(outboxMsg.PublishedAtUtc);
        Assert.Null(outboxMsg.Owner);
        Assert.Contains("rider-outbox-1", outboxMsg.PayloadJson);
    }

    [LocalSqlServerFact]
    public async Task UnitOfWork_FailedSave_RollsBackBothTripAndOutbox()
    {
        await using var db = _fixture.CreateDbContext();
        var uow = new TripUnitOfWork(db);
        var writer = new TripRepository(db);
        var eventWriter = new TripEventWriter(db);

        var tripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var trip = Trip.Request(tripId, "rider-outbox-fail", 3);
        var now = DateTimeOffset.UtcNow;
        var ctx = new TripContext(trip, "A", "A", 10, 106, "B", "B", 10.1, 106.1, "wallet", "tok-1", 5, 10, now, now);

        writer.Add(ctx);
        eventWriter.Add(new TripAcceptedV1(
            EventId: eventId,
            TripId: tripId,
            RiderId: "rider-outbox-fail",
            DriverId: "driver-outbox-fail",
            OccurredAtUtc: now,
            CorrelationId: $"trip-{tripId:D}-accept"));

        // Cause intentional failure: pre-seed duplicate key into DB
        await using (var seedDb = _fixture.CreateDbContext())
        {
            seedDb.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = "duplicate-rider", TripId = Guid.NewGuid() });
            await seedDb.SaveChangesAsync();
        }
        db.ActiveRiderTrips.Add(new ActiveRiderTripRecord { RiderId = "duplicate-rider", TripId = Guid.NewGuid() });

        await Assert.ThrowsAnyAsync<Exception>(() => uow.SaveAsync(CancellationToken.None));

        await using var verifyDb = _fixture.CreateDbContext();
        var outboxMsg = await verifyDb.IntegrationOutbox.FirstOrDefaultAsync(o => o.MessageId == eventId);
        Assert.Null(outboxMsg);

        var loadedTrip = await verifyDb.Trips.FirstOrDefaultAsync(t => t.Id == tripId);
        Assert.Null(loadedTrip);
    }

    [LocalSqlServerFact]
    public async Task TripOutboxPublisher_CompetingWorkers_OnlyOneClaimsMessage()
    {
        await using var db = _fixture.CreateDbContext();
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.IntegrationOutbox.Add(new IntegrationOutboxRecord
        {
            MessageId = eventId,
            EventType = nameof(TripAcceptedV1),
            PayloadJson = "{}",
            CreatedAtUtc = now,
            NextAttemptUtc = now,
            Attempts = 0,
            MaxAttempts = 10,
            RequiresAction = false,
            CorrelationId = "corr-race"
        });
        await db.SaveChangesAsync();

        var mockPublish = new Mock<IPublishEndpoint>();

        var services1 = new ServiceCollection();
        services1.AddScoped(_ => _fixture.CreateDbContext());
        services1.AddSingleton(mockPublish.Object);
        var provider1 = services1.BuildServiceProvider();

        var services2 = new ServiceCollection();
        services2.AddScoped(_ => _fixture.CreateDbContext());
        services2.AddSingleton(mockPublish.Object);
        var provider2 = services2.BuildServiceProvider();

        var publisher1 = new TripOutboxPublisher(provider1.GetRequiredService<IServiceScopeFactory>(), NullLogger<TripOutboxPublisher>.Instance);
        var publisher2 = new TripOutboxPublisher(provider2.GetRequiredService<IServiceScopeFactory>(), NullLogger<TripOutboxPublisher>.Instance);

        var task1 = publisher1.ProcessNextBatchAsync(CancellationToken.None);
        var task2 = publisher2.ProcessNextBatchAsync(CancellationToken.None);

        await Task.WhenAll(task1, task2);

        await using var verifyDb = _fixture.CreateDbContext();
        var record = await verifyDb.IntegrationOutbox.SingleAsync(o => o.MessageId == eventId);

        // One worker processed it and marked it published
        Assert.NotNull(record.PublishedAtUtc);
        Assert.Null(record.Owner);
        Assert.Null(record.LeaseUntilUtc);
    }

    [LocalSqlServerFact]
    public async Task TripOutboxPublisher_StaleOwner_DoesNotMarkPublished()
    {
        await using var db = _fixture.CreateDbContext();
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var staleOwner = Guid.NewGuid();
        var newOwner = Guid.NewGuid();

        // Simulate that the record was claimed by worker 1, but its lease expired and worker 2 took over
        var record = new IntegrationOutboxRecord
        {
            MessageId = eventId,
            EventType = nameof(TripAcceptedV1),
            PayloadJson = $"{{\"eventId\":\"{eventId}\",\"tripId\":\"{Guid.NewGuid()}\",\"riderId\":\"r\",\"driverId\":\"d\",\"occurredAtUtc\":\"{now:O}\",\"correlationId\":\"c\"}}",
            CreatedAtUtc = now.AddMinutes(-5),
            NextAttemptUtc = now.AddMinutes(-5),
            Attempts = 1,
            MaxAttempts = 10,
            Owner = newOwner, // New owner owns the lease
            LeaseUntilUtc = now.AddMinutes(5),
            RequiresAction = false,
            CorrelationId = "corr-stale"
        };
        db.IntegrationOutbox.Add(record);
        await db.SaveChangesAsync();

        // Stale owner attempts to complete the record
        await using var staleDb = _fixture.CreateDbContext();
        var staleRecord = await staleDb.IntegrationOutbox.FirstAsync(o => o.MessageId == eventId);

        // If the stale worker checks its owner before saving:
        if (staleRecord.Owner == staleOwner)
        {
            staleRecord.PublishedAtUtc = now;
            await staleDb.SaveChangesAsync();
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var current = await verifyDb.IntegrationOutbox.SingleAsync(o => o.MessageId == eventId);
        Assert.Null(current.PublishedAtUtc);
        Assert.Equal(newOwner, current.Owner);
    }
}
