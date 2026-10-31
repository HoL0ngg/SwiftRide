using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Consumers;
using SwiftRide.TripService.Infrastructure.Persistence;
using SwiftRide.TripService.Tests.Infrastructure;
using Xunit;

namespace SwiftRide.TripService.Tests.Messaging;

[Collection("TripDatabase")]
public class TripCompensationConsumerTests
{
    private readonly TripDatabaseFixture _fixture;

    public TripCompensationConsumerTests(TripDatabaseFixture fixture)
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

    private async Task SeedTripWithJobAsync(Guid tripId)
    {
        await using var db = _fixture.CreateDbContext();
        var workStore = new TripWorkStore(db);
        var uow = new TripUnitOfWork(db);
        var writer = new TripRepository(db);

        var now = DateTimeOffset.UtcNow;
        var trip = Trip.Request(tripId, "rider-comp", 3);
        trip.ApplyQuote(new FareSnapshot(Guid.NewGuid(), 50_000m, "VND"), "driver-comp");
        trip.ConfirmQuote("rider-comp", trip.Fare!.QuoteId, "driver-comp");
        trip.AcceptDriver("driver-comp");
        trip.StartEnRoute("driver-comp");
        trip.Pickup("driver-comp");
        trip.Dropoff("driver-comp", now);
        trip.RequestPayment();

        var ctx = new TripContext(trip, "P", "A", 10.7, 106.6, "D", "A", 10.8, 106.7, "card", "tok", 5m, 15m, now, now, paymentTransport: "Events");
        writer.Add(ctx);
        workStore.AddPaymentJob(tripId, $"trip-{tripId:D}-payment", now, 10, now.AddMinutes(10), "Events");
        await uow.SaveAsync(CancellationToken.None);
    }

    [LocalSqlServerFact]
    public async Task Consume_PaymentRefundedV1_CompletesJobAndInbox()
    {
        var tripId = Guid.NewGuid();
        await SeedTripWithJobAsync(tripId);

        var eventId = Guid.NewGuid();
        var message = new PaymentRefundedV1(
            EventId: eventId,
            RefundId: Guid.NewGuid(),
            PaymentId: Guid.NewGuid(),
            TripId: tripId,
            Amount: 50_000m,
            Currency: "VND",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}-refund");

        await using (var db = _fixture.CreateDbContext())
        {
            var workStore = new TripWorkStore(db);
            var uow = new TripUnitOfWork(db);
            var assignmentStore = new ActiveAssignmentStore(db);
            var consumer = new PaymentRefundedConsumer(workStore, uow, assignmentStore, db, NullLogger<PaymentRefundedConsumer>.Instance);

            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var inbox = await verifyDb.IntegrationInbox.FirstOrDefaultAsync(i => i.MessageId == eventId);
        Assert.NotNull(inbox);
        Assert.NotNull(inbox.ProcessedAtUtc);

        var job = await verifyDb.TripPaymentJobs.FirstAsync(j => j.TripId == tripId);
        Assert.Equal("Refunded", job.Status);
    }

    [LocalSqlServerFact]
    public async Task Consume_PaymentCompensationFailedV1_StagesRequiresAction()
    {
        var tripId = Guid.NewGuid();
        await SeedTripWithJobAsync(tripId);

        var eventId = Guid.NewGuid();
        var message = new PaymentCompensationFailedV1(
            EventId: eventId,
            RefundId: null,
            PaymentId: Guid.NewGuid(),
            TripId: tripId,
            ReasonCode: "card_refund_declined",
            SafeErrorMessage: "Gateway rejected refund",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}-compfail");

        await using (var db = _fixture.CreateDbContext())
        {
            var workStore = new TripWorkStore(db);
            var uow = new TripUnitOfWork(db);
            var consumer = new PaymentCompensationFailedConsumer(workStore, uow, db, NullLogger<PaymentCompensationFailedConsumer>.Instance);

            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var inbox = await verifyDb.IntegrationInbox.FirstOrDefaultAsync(i => i.MessageId == eventId);
        Assert.NotNull(inbox);
        Assert.NotNull(inbox.ProcessedAtUtc);
        Assert.Equal("Gateway rejected refund", inbox.LastError);

        var job = await verifyDb.TripPaymentJobs.FirstAsync(j => j.TripId == tripId);
        Assert.True(job.RequiresAction);
    }
}
