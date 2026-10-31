using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments;
using SwiftRide.PaymentService.Application.Payments.UseCases;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
using SwiftRide.PaymentService.Infrastructure.Consumers;
using SwiftRide.PaymentService.Infrastructure.Gateways;
using SwiftRide.PaymentService.Infrastructure.Messaging;
using SwiftRide.PaymentService.Infrastructure.Persistence;
using SwiftRide.PaymentService.Tests.Infrastructure;
using Xunit;

namespace SwiftRide.PaymentService.Tests.Messaging;

[Collection("PaymentDatabase")]
public class RideCompletedConsumerTests
{
    private readonly PaymentDatabaseFixture _fixture;

    public RideCompletedConsumerTests(PaymentDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class TestContextFactory(string connectionString) : IDbContextFactory<PaymentDbContext>
    {
        public PaymentDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            return new PaymentDbContext(options);
        }
    }

    private (RideCompletedConsumer consumer, PaymentDbContext db) CreateSut()
    {
        var db = _fixture.CreateDbContext();
        var reader = new PaymentRepository(db);
        var writer = new PaymentRepository(db);
        var uow = new PaymentUnitOfWork(db);
        var processingStore = new PaymentProcessingStore(db);
        var factory = new TestContextFactory(_fixture.ConnectionString!);
        var mockStore = new MockOperationStore(factory);

        var cardGateway = new MockCardGateway(mockStore);
        var walletGateway = new MockWalletGateway(mockStore);
        var gatewayFactory = new PaymentGatewayFactory(new IPaymentGateway[] { cardGateway, walletGateway });

        var useCase = new ProcessPaymentUseCase(
            reader,
            writer,
            uow,
            gatewayFactory,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System,
            NullLogger<ProcessPaymentUseCase>.Instance,
            processingStore);

        var eventWriter = new PaymentEventWriter(db);
        var consumer = new RideCompletedConsumer(
            useCase,
            db,
            eventWriter,
            NullLogger<RideCompletedConsumer>.Instance);

        return (consumer, db);
    }

    private static ConsumeContext<RideCompletedV1> CreateConsumeContext(RideCompletedV1 message)
    {
        var mock = new Mock<ConsumeContext<RideCompletedV1>>();
        mock.Setup(c => c.Message).Returns(message);
        mock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return mock.Object;
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_FirstArrival_ProcessesPayment_WritesInboxAndOutboxSettled()
    {
        await _fixture.ResetTablesAsync();
        var (consumer, db) = CreateSut();
        await using (db)
        {
            var tripId = Guid.NewGuid();
            var eventId = Guid.NewGuid();
            var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

            var message = new RideCompletedV1(
                EventId: eventId,
                TripId: tripId,
                RiderId: "rider-c1",
                DriverId: "driver-c1",
                QuoteId: Guid.NewGuid(),
                FareAmount: 60_000m,
                FareCurrency: "VND",
                PaymentMethod: "Card",
                MockTokenAlias: "tok_card_ok",
                PaymentIdempotencyKey: idempotencyKey,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"trip-{tripId:D}-complete");

            await consumer.Consume(CreateConsumeContext(message));

            await using var verifyDb = _fixture.CreateDbContext();
            // 1. Inbox recorded and marked processed
            var inbox = await verifyDb.IntegrationInbox
                .FirstOrDefaultAsync(i => i.ConsumerName == typeof(RideCompletedConsumer).FullName && i.MessageId == eventId);
            Assert.NotNull(inbox);
            Assert.NotNull(inbox.ProcessedAtUtc);

            // 2. Outbox has PaymentSettledV1
            var outbox = await verifyDb.IntegrationOutbox
                .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentSettledV1) && o.CorrelationId == message.CorrelationId);
            Assert.NotNull(outbox);
            Assert.Contains(tripId.ToString(), outbox.PayloadJson);

            // 3. Payment settled with balanced ledger
            var payment = await verifyDb.Payments
                .Include(p => p.LedgerEntries)
                .FirstOrDefaultAsync(p => p.TripId == tripId);
            Assert.NotNull(payment);
            Assert.Equal(PaymentStatus.Settled, payment.Status);
            Assert.Equal(2, payment.LedgerEntries.Count);
            Assert.True(payment.IsLedgerBalanced());
        }
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_DuplicateArrival_IsDeduplicated_DoesNotRechargeOrDuplicateOutbox()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

        var message = new RideCompletedV1(
            EventId: eventId,
            TripId: tripId,
            RiderId: "rider-c2",
            DriverId: "driver-c2",
            QuoteId: Guid.NewGuid(),
            FareAmount: 85_000m,
            FareCurrency: "VND",
            PaymentMethod: "Card",
            MockTokenAlias: "tok_card_ok",
            PaymentIdempotencyKey: idempotencyKey,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}-complete");

        // First consume
        var (consumer1, db1) = CreateSut();
        await using (db1)
        {
            await consumer1.Consume(CreateConsumeContext(message));
        }

        // Second consume with exact same event message
        var (consumer2, db2) = CreateSut();
        await using (db2)
        {
            await consumer2.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var outboxCount = await verifyDb.IntegrationOutbox.CountAsync();
        Assert.Equal(1, outboxCount);

        var paymentCount = await verifyDb.Payments.CountAsync();
        Assert.Equal(1, paymentCount);

        var ledgerCount = await verifyDb.LedgerEntries.CountAsync();
        Assert.Equal(2, ledgerCount);
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_DeclinedToken_WritesFailedPaymentAndPaymentFailedV1()
    {
        await _fixture.ResetTablesAsync();
        var (consumer, db) = CreateSut();
        await using (db)
        {
            var tripId = Guid.NewGuid();
            var eventId = Guid.NewGuid();
            var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

            var message = new RideCompletedV1(
                EventId: eventId,
                TripId: tripId,
                RiderId: "rider-declined",
                DriverId: "driver-declined",
                QuoteId: Guid.NewGuid(),
                FareAmount: 40_000m,
                FareCurrency: "VND",
                PaymentMethod: "Card",
                MockTokenAlias: "tok_card_declined",
                PaymentIdempotencyKey: idempotencyKey,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"trip-{tripId:D}-complete");

            await consumer.Consume(CreateConsumeContext(message));

            await using var verifyDb = _fixture.CreateDbContext();
            // Outbox has PaymentFailedV1
            var outbox = await verifyDb.IntegrationOutbox
                .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentFailedV1));
            Assert.NotNull(outbox);
            Assert.Contains(tripId.ToString(), outbox.PayloadJson);

            // Payment is Failed
            var payment = await verifyDb.Payments.FirstOrDefaultAsync(p => p.TripId == tripId);
            Assert.NotNull(payment);
            Assert.Equal(PaymentStatus.Failed, payment.Status);
        }
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_ZeroFare_AutoSettles_WritesPaymentSettledV1()
    {
        await _fixture.ResetTablesAsync();
        var (consumer, db) = CreateSut();
        await using (db)
        {
            var tripId = Guid.NewGuid();
            var eventId = Guid.NewGuid();
            var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

            var message = new RideCompletedV1(
                EventId: eventId,
                TripId: tripId,
                RiderId: "rider-zero",
                DriverId: "driver-zero",
                QuoteId: Guid.NewGuid(),
                FareAmount: 0m,
                FareCurrency: "VND",
                PaymentMethod: "Card",
                MockTokenAlias: "tok_card_ok",
                PaymentIdempotencyKey: idempotencyKey,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"trip-{tripId:D}-zero");

            await consumer.Consume(CreateConsumeContext(message));

            await using var verifyDb = _fixture.CreateDbContext();
            var payment = await verifyDb.Payments.FirstOrDefaultAsync(p => p.TripId == tripId);
            Assert.NotNull(payment);
            Assert.Equal(PaymentStatus.Settled, payment.Status);
            Assert.Equal(0m, payment.Amount.Amount);

            var outbox = await verifyDb.IntegrationOutbox
                .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentSettledV1));
            Assert.NotNull(outbox);
            Assert.Contains("rider-zero", outbox.PayloadJson);
        }
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_CurrentlyLeasedPayment_ThrowsTransientPaymentPendingException_DoesNotCompleteInbox()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

        // Pre-create payment and lease it by an external worker
        await using (var dbInit = _fixture.CreateDbContext())
        {
            var p = Payment.Create(tripId, "rider-leased", "driver-leased", Money.Create(50_000m, "VND"), PaymentMethod.Card, idempotencyKey, "tok_card_ok", DateTimeOffset.UtcNow);
            paymentId = p.Id;
            dbInit.Payments.Add(p);

            var procStore = new PaymentProcessingStore(dbInit);
            await procStore.InsertAsync(paymentId, p.RequestFingerprint, "driver-leased", DateTimeOffset.UtcNow, CancellationToken.None);
            await dbInit.SaveChangesAsync();

            // Worker claims 60s lease
            var claim = await procStore.TryClaimAsync(paymentId, Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60), 3, CancellationToken.None);
            Assert.Equal(ClaimStatus.Claimed, claim.Status);
        }

        var message = new RideCompletedV1(
            EventId: eventId,
            TripId: tripId,
            RiderId: "rider-leased",
            DriverId: "driver-leased",
            QuoteId: Guid.NewGuid(),
            FareAmount: 50_000m,
            FareCurrency: "VND",
            PaymentMethod: "Card",
            MockTokenAlias: "tok_card_ok",
            PaymentIdempotencyKey: idempotencyKey,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}-leased");

        var (consumer, db) = CreateSut();
        await using (db)
        {
            // Consumer MUST throw TransientPaymentPendingException to tell broker to retry with backoff
            await Assert.ThrowsAsync<TransientPaymentPendingException>(() => consumer.Consume(CreateConsumeContext(message)));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        // Inbox record exists but ProcessedAtUtc MUST BE NULL (not completed!)
        var inbox = await verifyDb.IntegrationInbox
            .FirstOrDefaultAsync(i => i.MessageId == eventId);
        Assert.NotNull(inbox);
        Assert.Null(inbox.ProcessedAtUtc);

        // No false outbox outcome emitted
        var outboxCount = await verifyDb.IntegrationOutbox.CountAsync();
        Assert.Equal(0, outboxCount);
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_ExhaustedRetries_StagesPaymentFailedV1_AndCompletesInbox()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

        // Pre-create payment with 3 attempts exhausted
        await using (var dbInit = _fixture.CreateDbContext())
        {
            var p = Payment.Create(tripId, "rider-exhausted", "driver-exhausted", Money.Create(50_000m, "VND"), PaymentMethod.Card, idempotencyKey, "tok_card_ok", DateTimeOffset.UtcNow);
            paymentId = p.Id;
            dbInit.Payments.Add(p);

            var procStore = new PaymentProcessingStore(dbInit);
            await procStore.InsertAsync(paymentId, p.RequestFingerprint, "driver-exhausted", DateTimeOffset.UtcNow, CancellationToken.None);
            await dbInit.SaveChangesAsync();

            // Simulate 3 claims to exhaust budget
            await procStore.TryClaimAsync(paymentId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromSeconds(1), 3, CancellationToken.None);
            await procStore.TryClaimAsync(paymentId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-3), TimeSpan.FromSeconds(1), 3, CancellationToken.None);
            await procStore.TryClaimAsync(paymentId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.FromSeconds(1), 3, CancellationToken.None);
        }

        var message = new RideCompletedV1(
            EventId: eventId,
            TripId: tripId,
            RiderId: "rider-exhausted",
            DriverId: "driver-exhausted",
            QuoteId: Guid.NewGuid(),
            FareAmount: 50_000m,
            FareCurrency: "VND",
            PaymentMethod: "Card",
            MockTokenAlias: "tok_card_ok",
            PaymentIdempotencyKey: idempotencyKey,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}-exhausted");

        var (consumer, db) = CreateSut();
        await using (db)
        {
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        // Inbox marked processed
        var inbox = await verifyDb.IntegrationInbox
            .FirstOrDefaultAsync(i => i.MessageId == eventId);
        Assert.NotNull(inbox);
        Assert.NotNull(inbox.ProcessedAtUtc);

        // Outbox contains PaymentFailedV1 with max_attempts_exceeded
        var outbox = await verifyDb.IntegrationOutbox
            .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentFailedV1));
        Assert.NotNull(outbox);
        Assert.Contains("max_attempts_exceeded", outbox.PayloadJson);
    }
}
