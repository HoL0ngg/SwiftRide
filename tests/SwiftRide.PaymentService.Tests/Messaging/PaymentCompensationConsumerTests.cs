using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Application.Abstractions;
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
public class PaymentCompensationConsumerTests
{
    private readonly PaymentDatabaseFixture _fixture;

    public PaymentCompensationConsumerTests(PaymentDatabaseFixture fixture)
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

    private (PaymentCompensationConsumer consumer, PaymentDbContext db) CreateSut()
    {
        var db = _fixture.CreateDbContext();
        var reader = new PaymentRepository(db);
        var uow = new PaymentUnitOfWork(db);
        var refundStore = new RefundProcessingStore(db);
        var factory = new TestContextFactory(_fixture.ConnectionString!);
        var mockStore = new MockOperationStore(factory);

        var cardGateway = new MockCardGateway(mockStore);
        var walletGateway = new MockWalletGateway(mockStore);
        var gatewayFactory = new PaymentGatewayFactory(new IPaymentGateway[] { cardGateway, walletGateway });

        var refundUseCase = new RefundPaymentUseCase(
            reader,
            uow,
            gatewayFactory,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System,
            NullLogger<RefundPaymentUseCase>.Instance,
            refundStore);

        var eventWriter = new PaymentEventWriter(db);
        var consumer = new PaymentCompensationConsumer(
            refundUseCase,
            db,
            eventWriter,
            NullLogger<PaymentCompensationConsumer>.Instance);

        return (consumer, db);
    }

    private static ConsumeContext<PaymentCompensationRequestedV1> CreateConsumeContext(PaymentCompensationRequestedV1 message)
    {
        var mock = new Mock<ConsumeContext<PaymentCompensationRequestedV1>>();
        mock.Setup(c => c.Message).Returns(message);
        mock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return mock.Object;
    }

    private async Task<Payment> SeedSettledPaymentAsync(Guid tripId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var payment = Payment.Create(tripId, "rider-comp-1", "driver-comp-1",
            Money.Create(amount, "VND"), PaymentMethod.Card, PaymentIdempotency.KeyForTrip(tripId), "tok_card_ok", now);
        payment.MarkSettled("gw-ref-comp", now);

        db.Payments.Add(payment);
        db.LedgerEntries.AddRange(payment.LedgerEntries);
        await db.SaveChangesAsync();
        return payment;
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_SettledPayment_Succeeds_WritesRefundAndPaymentRefundedV1()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        const decimal amount = 80_000m;
        var payment = await SeedSettledPaymentAsync(tripId, amount);

        var message = new PaymentCompensationRequestedV1(
            EventId: eventId,
            TripId: tripId,
            PaymentId: payment.Id,
            PaymentKey: PaymentIdempotency.KeyForTrip(tripId),
            Amount: amount,
            Currency: "VND",
            ReasonCode: "trip_cancelled_after_charge",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}-comp");

        var (consumer, db) = CreateSut();
        await using (db)
        {
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        // 1. Inbox recorded
        var inbox = await verifyDb.IntegrationInbox
            .FirstOrDefaultAsync(i => i.MessageId == eventId);
        Assert.NotNull(inbox);
        Assert.NotNull(inbox.ProcessedAtUtc);

        // 2. Refund record in DB
        var refund = await verifyDb.Refunds
            .Include(r => r.LedgerEntries)
            .FirstOrDefaultAsync(r => r.PaymentId == payment.Id);
        Assert.NotNull(refund);
        Assert.Equal(RefundStatus.Settled, refund.Status);
        Assert.Equal(2, refund.LedgerEntries.Count);
        Assert.Contains(refund.LedgerEntries, e => e.Direction == EntryDirection.Debit && e.Amount.Amount == amount);
        Assert.Contains(refund.LedgerEntries, e => e.Direction == EntryDirection.Credit && e.Amount.Amount == amount);

        // 3. Outbox event PaymentRefundedV1 staged
        var outbox = await verifyDb.IntegrationOutbox
            .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentRefundedV1));
        Assert.NotNull(outbox);
        Assert.Contains(payment.Id.ToString(), outbox.PayloadJson);

        // 4. Verify refund_processing table recorded outcome settled
        var processing = await verifyDb.RefundProcessings
            .FirstOrDefaultAsync(p => p.RefundId == refund.Id);
        Assert.NotNull(processing);
        Assert.Equal("settled", processing.Outcome);
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_MismatchedTripIdOrKey_RefusesRefund_EmitsPaymentCompensationFailedV1()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var mismatchedTripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        const decimal amount = 50_000m;
        var payment = await SeedSettledPaymentAsync(tripId, amount);

        // Mismatched TripId in compensation message
        var message = new PaymentCompensationRequestedV1(
            EventId: eventId,
            TripId: mismatchedTripId,
            PaymentId: payment.Id,
            PaymentKey: PaymentIdempotency.KeyForTrip(tripId),
            Amount: amount,
            Currency: "VND",
            ReasonCode: "fake_refund_request",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{mismatchedTripId:D}-comp");

        var (consumer, db) = CreateSut();
        await using (db)
        {
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        // Refund was NOT created
        var refund = await verifyDb.Refunds.FirstOrDefaultAsync(r => r.PaymentId == payment.Id);
        Assert.Null(refund);

        // Outbox contains PaymentCompensationFailedV1
        var outbox = await verifyDb.IntegrationOutbox
            .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentCompensationFailedV1));
        Assert.NotNull(outbox);
        Assert.Contains("does not belong to Trip", outbox.PayloadJson);
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_DuplicateArrival_IsDeduplicated()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        const decimal amount = 55_000m;
        var payment = await SeedSettledPaymentAsync(tripId, amount);

        var message = new PaymentCompensationRequestedV1(
            EventId: eventId,
            TripId: tripId,
            PaymentId: payment.Id,
            PaymentKey: PaymentIdempotency.KeyForTrip(tripId),
            Amount: amount,
            Currency: "VND",
            ReasonCode: "trip_cancelled_dup",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}-comp");

        // First consume
        var (consumer1, db1) = CreateSut();
        await using (db1)
        {
            await consumer1.Consume(CreateConsumeContext(message));
        }

        // Second consume with exact same event
        var (consumer2, db2) = CreateSut();
        await using (db2)
        {
            await consumer2.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var outboxCount = await verifyDb.IntegrationOutbox.CountAsync();
        Assert.Equal(1, outboxCount);

        var refundCount = await verifyDb.Refunds.CountAsync();
        Assert.Equal(1, refundCount);
    }

    [LocalPostgresPaymentFact]
    public async Task Consume_PaymentNotFound_EmitsPaymentCompensationFailedV1()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var nonExistentPaymentId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var message = new PaymentCompensationRequestedV1(
            EventId: eventId,
            TripId: tripId,
            PaymentId: nonExistentPaymentId,
            PaymentKey: "non-existent-key",
            Amount: 50_000m,
            Currency: "VND",
            ReasonCode: "cancel_not_found",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: $"trip-{tripId:D}-notfound");

        var (consumer, db) = CreateSut();
        await using (db)
        {
            await consumer.Consume(CreateConsumeContext(message));
        }

        await using var verifyDb = _fixture.CreateDbContext();
        var outbox = await verifyDb.IntegrationOutbox
            .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentCompensationFailedV1));
        Assert.NotNull(outbox);
        Assert.Contains(tripId.ToString(), outbox.PayloadJson);
    }
}
