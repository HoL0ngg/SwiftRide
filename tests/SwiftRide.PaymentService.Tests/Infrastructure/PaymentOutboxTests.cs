using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
using SwiftRide.PaymentService.Infrastructure.Messaging;
using SwiftRide.PaymentService.Infrastructure.Persistence;
using Xunit;

namespace SwiftRide.PaymentService.Tests.Infrastructure;

[Collection("PaymentDatabase")]
public class PaymentOutboxTests
{
    private readonly PaymentDatabaseFixture _fixture;

    public PaymentOutboxTests(PaymentDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [LocalPostgresPaymentFact]
    public async Task UnitOfWork_SuccessfulSave_PersistsBothPaymentAndOutboxAtomically()
    {
        await _fixture.ResetTablesAsync();
        await using var db = _fixture.CreateDbContext();

        var tripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = PaymentIdempotency.KeyForTrip(tripId);

        var payment = Payment.Create(tripId, "rider-outbox-1", "driver-outbox-1",
            Money.Create(75_000m, "VND"), PaymentMethod.Card, key, "tok_card_ok", now);
        db.Payments.Add(payment);

        var eventWriter = new PaymentEventWriter(db);
        eventWriter.Add(new PaymentSettledV1(
            EventId: eventId,
            PaymentId: payment.Id,
            TripId: tripId,
            RiderId: "rider-outbox-1",
            DriverId: "driver-outbox-1",
            Amount: 75_000m,
            Currency: "VND",
            PaymentKey: key,
            OccurredAtUtc: now,
            CorrelationId: $"trip-{tripId:D}-payment"));

        await db.SaveChangesAsync();

        await using var verifyDb = _fixture.CreateDbContext();
        var outboxMsg = await verifyDb.IntegrationOutbox.FirstOrDefaultAsync(o => o.MessageId == eventId);
        Assert.NotNull(outboxMsg);
        Assert.Equal(nameof(PaymentSettledV1), outboxMsg.EventType);
        Assert.Null(outboxMsg.PublishedAtUtc);
        Assert.Null(outboxMsg.Owner);
        Assert.Contains("rider-outbox-1", outboxMsg.PayloadJson);

        var loadedPayment = await verifyDb.Payments.FirstOrDefaultAsync(p => p.Id == payment.Id);
        Assert.NotNull(loadedPayment);
    }

    [LocalPostgresPaymentFact]
    public async Task UnitOfWork_FailedSave_RollsBackBothPaymentAndOutbox()
    {
        await _fixture.ResetTablesAsync();

        var tripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = PaymentIdempotency.KeyForTrip(tripId);

        // Pre-seed a payment with the same idempotency key into DB to cause unique constraint violation
        await using (var seedDb = _fixture.CreateDbContext())
        {
            var existingPayment = Payment.Create(tripId, "rider-outbox-seed", "driver-outbox-seed",
                Money.Create(50_000m, "VND"), PaymentMethod.Card, key, "tok_card_ok", now);
            seedDb.Payments.Add(existingPayment);
            await seedDb.SaveChangesAsync();
        }

        // Try saving another payment with same key AND an outbox message in one transaction
        await using (var db = _fixture.CreateDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                var duplicatePayment = Payment.Create(tripId, "rider-outbox-dup", "driver-outbox-dup",
                    Money.Create(60_000m, "VND"), PaymentMethod.Card, key, "tok_card_ok", now);
                db.Payments.Add(duplicatePayment);

                var eventWriter = new PaymentEventWriter(db);
                eventWriter.Add(new PaymentSettledV1(
                    EventId: eventId,
                    PaymentId: duplicatePayment.Id,
                    TripId: tripId,
                    RiderId: "rider-outbox-dup",
                    DriverId: "driver-outbox-dup",
                    Amount: 60_000m,
                    Currency: "VND",
                    PaymentKey: key,
                    OccurredAtUtc: now,
                    CorrelationId: $"trip-{tripId:D}-payment"));

                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
            }
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var outboxMsg = await verifyDb.IntegrationOutbox.FirstOrDefaultAsync(o => o.MessageId == eventId);
        Assert.Null(outboxMsg);
    }

    [LocalPostgresPaymentFact]
    public async Task Publisher_PublishesMessage_AndMarksPublishedAtUtc()
    {
        await _fixture.ResetTablesAsync();
        var eventId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = PaymentIdempotency.KeyForTrip(tripId);

        await using (var db = _fixture.CreateDbContext())
        {
            var eventWriter = new PaymentEventWriter(db);
            eventWriter.Add(new PaymentSettledV1(
                EventId: eventId,
                PaymentId: Guid.NewGuid(),
                TripId: tripId,
                RiderId: "rider-pub-1",
                DriverId: "driver-pub-1",
                Amount: 50_000m,
                Currency: "VND",
                PaymentKey: key,
                OccurredAtUtc: now,
                CorrelationId: $"trip-{tripId:D}"));
            await db.SaveChangesAsync();
        }

        var publishMock = new Mock<IPublishEndpoint>();
        publishMock.Setup(p => p.Publish(It.IsAny<PaymentSettledV1>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateDbContext());
        services.AddSingleton(publishMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var publisher = new PaymentOutboxPublisher(scopeFactory, NullLogger<PaymentOutboxPublisher>.Instance);
        var processed = await publisher.ProcessNextBatchAsync(CancellationToken.None);

        Assert.True(processed);

        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var record = await verifyDb.IntegrationOutbox.FirstAsync(o => o.MessageId == eventId);
            Assert.NotNull(record.PublishedAtUtc);
            Assert.Null(record.Owner);
            Assert.Null(record.LeaseUntilUtc);
            Assert.Null(record.LastError);
        }

        publishMock.Verify(p => p.Publish(It.Is<PaymentSettledV1>(e => e.TripId == tripId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [LocalPostgresPaymentFact]
    public async Task Publisher_StaleOwner_DoesNotCommitPublishedStatus()
    {
        await _fixture.ResetTablesAsync();
        var eventId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = PaymentIdempotency.KeyForTrip(tripId);

        await using (var db = _fixture.CreateDbContext())
        {
            var eventWriter = new PaymentEventWriter(db);
            eventWriter.Add(new PaymentSettledV1(
                EventId: eventId,
                PaymentId: Guid.NewGuid(),
                TripId: tripId,
                RiderId: "rider-stale",
                DriverId: "driver-stale",
                Amount: 50_000m,
                Currency: "VND",
                PaymentKey: key,
                OccurredAtUtc: now,
                CorrelationId: $"trip-{tripId:D}"));
            await db.SaveChangesAsync();
        }

        var publishTcs = new TaskCompletionSource();
        var publishMock = new Mock<IPublishEndpoint>();
        publishMock.Setup(p => p.Publish(It.IsAny<PaymentSettledV1>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // While publish is pending, another node steals the lease
                await using var stealDb = _fixture.CreateDbContext();
                var record = await stealDb.IntegrationOutbox.FirstAsync(o => o.MessageId == eventId);
                record.Owner = Guid.NewGuid(); // new owner
                record.LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                await stealDb.SaveChangesAsync();
                publishTcs.SetResult();
            });

        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateDbContext());
        services.AddSingleton(publishMock.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var publisher = new PaymentOutboxPublisher(scopeFactory, NullLogger<PaymentOutboxPublisher>.Instance);
        await publisher.ProcessNextBatchAsync(CancellationToken.None);

        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var record = await verifyDb.IntegrationOutbox.FirstAsync(o => o.MessageId == eventId);
            // Must NOT mark published because original owner lost lease
            Assert.Null(record.PublishedAtUtc);
        }
    }
}
