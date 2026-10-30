using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Consumers;
using SwiftRide.TripService.Infrastructure.Messaging;
using SwiftRide.TripService.Infrastructure.Persistence;
using SwiftRide.TripService.Tests.Infrastructure;
using Xunit;

namespace SwiftRide.TripService.Tests.Messaging;

[Collection("TripDatabase")]
public class TripPaymentConsumerTests
{
    private readonly TripDatabaseFixture _fixture;

    public TripPaymentConsumerTests(TripDatabaseFixture fixture)
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

    private async Task SeedTripAtPaymentPendingAsync(Guid tripId, string riderId, string driverId, decimal fareAmount, string transport = "Events")
    {
        await using var db = _fixture.CreateDbContext();
        var writer = new TripRepository(db);
        var workStore = new TripWorkStore(db);
        var assignmentStore = new ActiveAssignmentStore(db);
        var uow = new TripUnitOfWork(db);

        var now = DateTimeOffset.UtcNow;
        var trip = Trip.Request(tripId, riderId, 3);
        trip.ApplyQuote(new FareSnapshot(Guid.NewGuid(), fareAmount, "VND"), driverId);
        trip.ConfirmQuote(riderId, trip.Fare!.QuoteId, driverId);
        trip.AcceptDriver(driverId);
        trip.StartEnRoute(driverId);
        trip.Pickup(driverId);
        trip.Dropoff(driverId, now);
        trip.RequestPayment();

        var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok_1", 5m, 15m, now, now, paymentTransport: transport);
        writer.Add(ctx);
        assignmentStore.ClaimRider(riderId, tripId);
        workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 10, now.AddMinutes(10), transport);

        await uow.SaveAsync(CancellationToken.None);
    }

    private static PaymentSettledConsumer CreateSettledConsumer(TripDbContext db, IPaymentClient? paymentClient = null, ITripMetrics? metrics = null)
    {
        var reader = new TripRepository(db);
        var writer = new TripRepository(db);
        var uow = new TripUnitOfWork(db);
        var workStore = new TripWorkStore(db);
        var assignmentStore = new ActiveAssignmentStore(db);
        var eventWriter = new TripEventWriter(db);
        var client = paymentClient ?? new Mock<IPaymentClient>().Object;

        return new PaymentSettledConsumer(
            reader,
            writer,
            uow,
            workStore,
            assignmentStore,
            eventWriter,
            client,
            db,
            NullLogger<PaymentSettledConsumer>.Instance,
            metrics);
    }

    [LocalSqlServerFact]
    public async Task PaymentSettledConsumer_TransitionsTripToPaid_ReleasesRider_AndAllowsSecondBooking()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-settle-1";
        var driverId = "driver-settle-1";
        const decimal fareAmount = 50_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        var eventId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var paymentKey = $"trip-{tripId:D}-payment";

        var message = new PaymentSettledV1(
            EventId: eventId,
            PaymentId: paymentId,
            TripId: tripId,
            RiderId: riderId,
            DriverId: driverId,
            Amount: fareAmount,
            Currency: "VND",
            PaymentKey: paymentKey,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}");

        await using (var db = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db);
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        // 1. Inbox recorded
        var inbox = await verifyDb.IntegrationInbox
            .FirstOrDefaultAsync(i => i.MessageId == eventId);
        Assert.NotNull(inbox);
        Assert.NotNull(inbox.ProcessedAtUtc);

        // 2. Trip transitioned to Paid
        var trip = await verifyDb.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("Paid", trip.Status);
        Assert.Equal(paymentId, trip.PaymentId);

        // 3. Payment job completed
        var job = await verifyDb.TripPaymentJobs.FirstAsync(j => j.TripId == tripId);
        Assert.Equal("Completed", job.Status);

        // 4. Rider released: active rider trips no longer holds rider
        var activeHeld = await verifyDb.ActiveRiderTrips.FirstOrDefaultAsync(a => a.RiderId == riderId);
        Assert.Null(activeHeld);

        // 5. Rider can now successfully book a second trip without conflict
        var trip2Id = Guid.NewGuid();
        var trip2 = Trip.Request(trip2Id, riderId, 3);
        var assignmentStore2 = new ActiveAssignmentStore(verifyDb);
        assignmentStore2.ClaimRider(riderId, trip2Id);
        var writer2 = new TripRepository(verifyDb);
        var uow2 = new TripUnitOfWork(verifyDb);
        var now2 = DateTimeOffset.UtcNow;
        var ctx2 = new TripContext(trip2, "P2", "A2", 10.7, 106.6, "D2", "A2", 10.8, 106.7, "card", "tok_2", 5m, 15m, now2, now2);
        writer2.Add(ctx2);
        await uow2.SaveAsync(CancellationToken.None);

        var activeTrip2 = await verifyDb.ActiveRiderTrips.FirstOrDefaultAsync(a => a.RiderId == riderId);
        Assert.NotNull(activeTrip2);
        Assert.Equal(trip2Id, activeTrip2.TripId);
    }

    [LocalSqlServerFact]
    public async Task PaymentSettledConsumer_DuplicateArrival_IsDeduplicated()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-settle-dup";
        var driverId = "driver-settle-dup";
        const decimal fareAmount = 70_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        var eventId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var paymentKey = $"trip-{tripId:D}-payment";

        var message = new PaymentSettledV1(
            EventId: eventId,
            PaymentId: paymentId,
            TripId: tripId,
            RiderId: riderId,
            DriverId: driverId,
            Amount: fareAmount,
            Currency: "VND",
            PaymentKey: paymentKey,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}");

        // First consume
        await using (var db1 = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db1);
            await consumer.Consume(CreateConsumeContext(message));
        }

        // Second consume with exact same event
        await using (var db2 = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db2);
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var trip = await verifyDb.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("Paid", trip.Status);

        var activeHeld = await verifyDb.ActiveRiderTrips.FirstOrDefaultAsync(a => a.RiderId == riderId);
        Assert.Null(activeHeld);
    }

    [LocalSqlServerFact]
    public async Task PaymentSettledConsumer_MismatchedRider_StagesRequiresAction_DoesNotSettle()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-settle-correct";
        var driverId = "driver-settle-correct";
        const decimal fareAmount = 50_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        var eventId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        // Spoofed/wrong RiderId in event
        var message = new PaymentSettledV1(
            EventId: eventId,
            PaymentId: paymentId,
            TripId: tripId,
            RiderId: "attacker-rider",
            DriverId: driverId,
            Amount: fareAmount,
            Currency: "VND",
            PaymentKey: $"trip-{tripId:D}-payment",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}");

        await using (var db = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db);
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var trip = await verifyDb.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("PaymentPending", trip.Status);

        var job = await verifyDb.TripPaymentJobs.FirstAsync(j => j.TripId == tripId);
        Assert.True(job.RequiresAction);
    }

    [LocalSqlServerFact]
    public async Task PaymentSettledConsumer_MismatchedDriver_StagesRequiresAction_DoesNotSettle()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-driver-correct";
        var driverId = "driver-correct";
        const decimal fareAmount = 50_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        var message = new PaymentSettledV1(
            EventId: Guid.NewGuid(),
            PaymentId: Guid.NewGuid(),
            TripId: tripId,
            RiderId: riderId,
            DriverId: "wrong-driver",
            Amount: fareAmount,
            Currency: "VND",
            PaymentKey: $"trip-{tripId:D}-payment",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}");

        await using (var db = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db);
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var trip = await verifyDb.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("PaymentPending", trip.Status);

        var job = await verifyDb.TripPaymentJobs.FirstAsync(j => j.TripId == tripId);
        Assert.True(job.RequiresAction);
    }

    [LocalSqlServerFact]
    public async Task PaymentSettledConsumer_LateArrival_AfterCompensationRequested_DoesNotTransitionToPaid()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-late-comp";
        var driverId = "driver-late-comp";
        const decimal fareAmount = 50_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        // Set job status to CompensationRequested
        await using (var db = _fixture.CreateDbContext())
        {
            var job = await db.TripPaymentJobs.FirstAsync(j => j.TripId == tripId);
            job.Status = "CompensationRequested";
            job.RequiresAction = true;
            await db.SaveChangesAsync();
        }

        // Late PaymentSettled arrives
        var message = new PaymentSettledV1(
            EventId: Guid.NewGuid(),
            PaymentId: Guid.NewGuid(),
            TripId: tripId,
            RiderId: riderId,
            DriverId: driverId,
            Amount: fareAmount,
            Currency: "VND",
            PaymentKey: $"trip-{tripId:D}-payment",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}");

        await using (var db = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db);
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var trip = await verifyDb.Trips.FirstAsync(t => t.Id == tripId);
        // Remains PaymentPending, NOT Paid
        Assert.Equal("PaymentPending", trip.Status);
    }

    [LocalSqlServerFact]
    public async Task PaymentSettledConsumer_FinancialMismatch_Verified_StagesCompensationRequest()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-mismatch";
        var driverId = "driver-mismatch";
        const decimal fareAmount = 50_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        var paymentId = Guid.NewGuid();
        var mockPaymentClient = new Mock<IPaymentClient>();
        // Remote payment returns settled with mismatched amount: 80,000 VND instead of agreed 50,000 VND
        mockPaymentClient.Setup(c => c.GetByTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                paymentId,
                tripId,
                riderId,
                driverId,
                80_000m,
                "VND",
                RemotePaymentStatus.Settled,
                null,
                false,
                null,
                null));

        var message = new PaymentSettledV1(
            EventId: Guid.NewGuid(),
            PaymentId: paymentId,
            TripId: tripId,
            RiderId: riderId,
            DriverId: driverId,
            Amount: 80_000m, // Mismatched amount
            Currency: "VND",
            PaymentKey: $"trip-{tripId:D}-payment",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}");

        await using (var db = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db, mockPaymentClient.Object);
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var trip = await verifyDb.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("PaymentPending", trip.Status);

        var job = await verifyDb.TripPaymentJobs.FirstAsync(j => j.TripId == tripId);
        Assert.Equal("CompensationRequested", job.Status);
        Assert.True(job.RequiresAction);

        // Verify PaymentCompensationRequestedV1 was staged in Outbox
        var outboxRecord = await verifyDb.IntegrationOutbox
            .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentCompensationRequestedV1));
        Assert.NotNull(outboxRecord);
        Assert.Contains(paymentId.ToString(), outboxRecord.PayloadJson);
        Assert.Contains("financial_mismatch", outboxRecord.PayloadJson);
    }

    [LocalSqlServerFact]
    public async Task PaymentFailedConsumer_PermanentFailure_MarksRequiresAction_TripRemainsPaymentPending()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-fail-perm";
        var driverId = "driver-fail-perm";
        const decimal fareAmount = 45_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        var eventId = Guid.NewGuid();
        var paymentKey = $"trip-{tripId:D}-payment";

        var message = new PaymentFailedV1(
            EventId: eventId,
            PaymentId: Guid.NewGuid(),
            TripId: tripId,
            PaymentKey: paymentKey,
            FailureCode: "CARD_EXPIRED",
            IsPermanent: true,
            SafeErrorMessage: "The card has expired.",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}");

        await using (var db = _fixture.CreateDbContext())
        {
            var reader = new TripRepository(db);
            var writer = new TripRepository(db);
            var uow = new TripUnitOfWork(db);
            var workStore = new TripWorkStore(db);

            var consumer = new PaymentFailedConsumer(
                reader,
                writer,
                uow,
                workStore,
                db,
                NullLogger<PaymentFailedConsumer>.Instance);

            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        // Trip is STILL PaymentPending per AGENTS.md rules
        var trip = await verifyDb.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("PaymentPending", trip.Status);
        Assert.Equal("Permanent", trip.LastPaymentFailure);

        // Job has RequiresAction=true
        var job = await verifyDb.TripPaymentJobs.FirstAsync(j => j.TripId == tripId);
        Assert.True(job.RequiresAction);
    }

    [LocalSqlServerFact]
    public async Task PaymentFailedConsumer_LateArrival_AfterTripPaid_DoesNotRevertPaid()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-late-fail";
        var driverId = "driver-late-fail";
        const decimal fareAmount = 60_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        // First, settle trip to Paid
        await using (var db = _fixture.CreateDbContext())
        {
            var settleConsumer = CreateSettledConsumer(db);
            await settleConsumer.Consume(CreateConsumeContext(new PaymentSettledV1(
                EventId: Guid.NewGuid(),
                PaymentId: Guid.NewGuid(),
                TripId: tripId,
                RiderId: riderId,
                DriverId: driverId,
                Amount: fareAmount,
                Currency: "VND",
                PaymentKey: $"trip-{tripId:D}-payment",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"trip-{tripId:D}")));
        }

        // Now, a delayed/out-of-order PaymentFailed arrives
        await using (var db2 = _fixture.CreateDbContext())
        {
            var reader = new TripRepository(db2);
            var writer = new TripRepository(db2);
            var uow = new TripUnitOfWork(db2);
            var workStore = new TripWorkStore(db2);

            var failConsumer = new PaymentFailedConsumer(reader, writer, uow, workStore, db2, NullLogger<PaymentFailedConsumer>.Instance);
            await failConsumer.Consume(CreateConsumeContext(new PaymentFailedV1(
                EventId: Guid.NewGuid(),
                PaymentId: Guid.NewGuid(),
                TripId: tripId,
                PaymentKey: $"trip-{tripId:D}-payment",
                FailureCode: "DELAYED_FAILURE",
                IsPermanent: true,
                SafeErrorMessage: "Arrived after settled",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"trip-{tripId:D}")));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var trip = await verifyDb.Trips.FirstAsync(t => t.Id == tripId);
        Assert.Equal("Paid", trip.Status);
    }

    [LocalSqlServerFact]
    public async Task PaymentSettledConsumer_MetricIncrementsOnlyOnce_NotOnReplay()
    {
        var tripId = Guid.NewGuid();
        var riderId = "rider-metric-test";
        var driverId = "driver-metric-test";
        const decimal fareAmount = 80_000m;
        await SeedTripAtPaymentPendingAsync(tripId, riderId, driverId, fareAmount, "Events");

        var mockMetrics = new Mock<ITripMetrics>();

        var paymentSettled = new PaymentSettledV1(
            EventId: Guid.NewGuid(),
            PaymentId: Guid.NewGuid(),
            TripId: tripId,
            RiderId: riderId,
            DriverId: driverId,
            Amount: fareAmount,
            Currency: "VND",
            PaymentKey: $"trip-{tripId:D}-payment",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}");

        // 1. Initial event transitions trip to Paid -> metrics.TripCompleted() called once
        await using (var db1 = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db1, metrics: mockMetrics.Object);
            await consumer.Consume(CreateConsumeContext(paymentSettled));
        }

        mockMetrics.Verify(m => m.TripCompleted(), Times.Once);

        // 2. Duplicate event arrives (replay) -> metrics.TripCompleted() MUST NOT be called again
        await using (var db2 = _fixture.CreateDbContext())
        {
            var consumer = CreateSettledConsumer(db2, metrics: mockMetrics.Object);
            await consumer.Consume(CreateConsumeContext(paymentSettled with { EventId = Guid.NewGuid() }));
        }

        mockMetrics.Verify(m => m.TripCompleted(), Times.Once); // Still exactly once
    }

    [LocalSqlServerFact]
    public async Task TripEventWriter_And_OutboxPublisher_Preserve_TraceParent_Context()
    {
        using var activitySource = new System.Diagnostics.ActivitySource("TestActivitySource");
        using var activityListener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(activityListener);

        using var parentActivity = activitySource.StartActivity("TripHttpRequest");
        Assert.NotNull(parentActivity);
        var expectedTraceId = parentActivity.TraceId.ToString();
        var expectedTraceParent = parentActivity.Id;

        var tripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        // 1. Writer captures TraceParent in Outbox record
        await using (var db = _fixture.CreateDbContext())
        {
            var writer = new TripEventWriter(db);
            writer.Add(new RideCompletedV1(
                EventId: eventId,
                TripId: tripId,
                RiderId: "rider-trace",
                DriverId: "driver-trace",
                QuoteId: Guid.NewGuid(),
                FareAmount: 50_000m,
                FareCurrency: "VND",
                PaymentMethod: "Card",
                MockTokenAlias: "tok-1",
                PaymentIdempotencyKey: $"trip-{tripId:D}-payment",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: "corr-1"));

            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var record = await db.IntegrationOutbox.FirstAsync(o => o.MessageId == eventId);
            Assert.Equal(expectedTraceParent, record.TraceParent);
            Assert.Contains(expectedTraceId, record.TraceParent!);
        }

        // 2. Publisher restores TraceParent and sets broker header
        var testEndpoint = new TestPublishEndpoint();

        var mockScopeFactory = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(() =>
        {
            var mockScope = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScope>();
            var mockServiceProvider = new Mock<IServiceProvider>();
            mockServiceProvider.Setup(sp => sp.GetService(typeof(TripDbContext))).Returns(_fixture.CreateDbContext());
            mockServiceProvider.Setup(sp => sp.GetService(typeof(IPublishEndpoint))).Returns(testEndpoint);
            mockScope.SetupGet(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
            return mockScope.Object;
        });

        var publisher = new TripOutboxPublisher(
            mockScopeFactory.Object,
            NullLogger<TripOutboxPublisher>.Instance);

        var processed = await publisher.ProcessNextBatchAsync(CancellationToken.None);
        Assert.True(processed);

        Assert.NotNull(testEndpoint.CapturedTraceParent);
        Assert.Contains(expectedTraceId, testEndpoint.CapturedTraceParent); // W3C distributed trace id preserved across outbox
    }

    private sealed class TestPublishEndpoint : IPublishEndpoint
    {
        public string? CapturedTraceParent { get; private set; }

        public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;

        public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class
        {
            var mockCtx = new Mock<PublishContext<T>>();
            var headers = new Dictionary<string, object>();
            var mockHeaders = new Mock<SendHeaders>();
            mockHeaders.Setup(h => h.Set(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((k, v) => headers[k] = v);
            mockHeaders.Setup(h => h.Set(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
                .Callback<string, object, bool>((k, v, _) => headers[k] = v);

            mockCtx.SetupGet(c => c.Headers).Returns(mockHeaders.Object);
            publishPipe.Send(mockCtx.Object).GetAwaiter().GetResult();

            if (headers.TryGetValue("traceparent", out var val))
                CapturedTraceParent = val?.ToString();

            return Task.CompletedTask;
        }

        public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class
        {
            var mockCtx = new Mock<PublishContext<T>>();
            var headers = new Dictionary<string, object>();
            var mockHeaders = new Mock<SendHeaders>();
            mockHeaders.Setup(h => h.Set(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
                .Callback<string, object, bool>((k, v, _) => headers[k] = v);

            mockCtx.SetupGet(c => c.Headers).Returns(mockHeaders.Object);
            publishPipe.Send(mockCtx.Object).GetAwaiter().GetResult();

            if (headers.TryGetValue("traceparent", out var val))
                CapturedTraceParent = val?.ToString();

            return Task.CompletedTask;
        }

        public Task Publish(object message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotImplementedException();
    }
}
