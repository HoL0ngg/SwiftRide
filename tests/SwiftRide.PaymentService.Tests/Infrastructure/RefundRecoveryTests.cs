using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Application.Payments.UseCases;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
using SwiftRide.PaymentService.Infrastructure.Gateways;
using SwiftRide.PaymentService.Infrastructure.Persistence;
using Xunit;

namespace SwiftRide.PaymentService.Tests.Infrastructure;

[Collection("PaymentDatabase")]
public class RefundRecoveryTests
{
    private readonly PaymentDatabaseFixture _fixture;

    public RefundRecoveryTests(PaymentDatabaseFixture fixture)
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

    private (RefundPaymentUseCase useCase, MockOperationStore mockStore) CreateSut(IPaymentGateway? customGateway = null)
    {
        var db = _fixture.CreateDbContext();
        var reader = new PaymentRepository(db);
        var uow = new PaymentUnitOfWork(db);
        var factory = new TestContextFactory(_fixture.ConnectionString!);
        var mockStore = new MockOperationStore(factory);

        var cardGateway = customGateway ?? new MockCardGateway(mockStore);
        var walletGateway = new MockWalletGateway(mockStore);
        var gatewayFactory = new PaymentGatewayFactory(new[] { cardGateway, walletGateway });

        var useCase = new RefundPaymentUseCase(
            reader,
            uow,
            gatewayFactory,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System,
            NullLogger<RefundPaymentUseCase>.Instance);

        return (useCase, mockStore);
    }

    private async Task<Payment> SeedSettledPaymentAsync(decimal amount = 85_000m)
    {
        await using var db = _fixture.CreateDbContext();
        var tripId = Guid.NewGuid();
        var payment = Payment.Create(
            tripId,
            "rider-refund-1",
            "driver-refund-1",
            Money.Create(amount, "VND"),
            PaymentMethod.Card,
            PaymentIdempotency.KeyForTrip(tripId),
            "tok_card_ok",
            DateTimeOffset.UtcNow);

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        payment.MarkSettled("gw-charge-ref-123", DateTimeOffset.UtcNow);
        db.LedgerEntries.AddRange(payment.LedgerEntries);
        await db.SaveChangesAsync();

        return payment;
    }

    [LocalPostgresPaymentFact]
    public async Task RestartAfterRefundSuccess_SettlesOnceAndLedgerReverses()
    {
        await _fixture.ResetTablesAsync();
        var payment = await SeedSettledPaymentAsync();
        const string reason = "Customer cancelled before trip started";

        // Host 1 executes refund
        var (sut1, _) = CreateSut();
        var result1 = await sut1.ExecuteAsync(payment.Id, reason, CancellationToken.None);

        Assert.False(result1.IsReplay);
        Assert.NotNull(result1.Payment.Refund);
        Assert.Equal("Settled", result1.Payment.Refund.Status);

        // Host restarts (Host 2 with fresh context on same DB)
        var (sut2, _) = CreateSut();
        var result2 = await sut2.ExecuteAsync(payment.Id, reason, CancellationToken.None);

        Assert.True(result2.IsReplay);
        Assert.NotNull(result2.Payment.Refund);
        Assert.Equal(result1.Payment.Refund.Id, result2.Payment.Refund.Id);
        Assert.Equal("Settled", result2.Payment.Refund.Status);

        // Verify database state: 1 payment, 1 refund, 2 charge entries, 2 refund reversal entries
        await using var db = _fixture.CreateDbContext();
        var refunds = await db.Refunds.Include(r => r.LedgerEntries).ToListAsync();
        Assert.Single(refunds);
        Assert.Equal(RefundStatus.Settled, refunds[0].Status);
        Assert.NotNull(refunds[0].SettledAt);
        Assert.Equal(2, refunds[0].LedgerEntries.Count);

        var debitEntry = refunds[0].LedgerEntries.Single(e => e.Direction == EntryDirection.Debit);
        var creditEntry = refunds[0].LedgerEntries.Single(e => e.Direction == EntryDirection.Credit);
        Assert.Equal("driver:driver-refund-1", debitEntry.Account);
        Assert.Equal("rider:rider-refund-1", creditEntry.Account);
        Assert.Equal(85_000m, debitEntry.Amount.Amount);
        Assert.Equal(85_000m, creditEntry.Amount.Amount);

        // Original payment and charge entries are untouched
        var payments = await db.Payments.Include(p => p.LedgerEntries).ToListAsync();
        Assert.Single(payments);
        Assert.Equal(PaymentStatus.Settled, payments[0].Status);
        Assert.Equal(2, payments[0].LedgerEntries.Count);
    }

    [LocalPostgresPaymentFact]
    public async Task ConcurrentRefunds_SameKey_OneWinnerAndReplays()
    {
        await _fixture.ResetTablesAsync();
        var payment = await SeedSettledPaymentAsync();
        const string reason = "Concurrent double refund test";

        // 10 concurrent requests
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            var (sut, _) = CreateSut();
            return await sut.ExecuteAsync(payment.Id, reason, CancellationToken.None);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        var nonReplays = results.Count(r => !r.IsReplay);
        var replays = results.Count(r => r.IsReplay);

        Assert.Equal(1, nonReplays);
        Assert.Equal(9, replays);
        Assert.All(results, r => Assert.Equal("Settled", r.Payment.Refund?.Status));

        // DB state: exactly 1 refund record and 2 refund reversal ledger entries
        await using var db = _fixture.CreateDbContext();
        var refunds = await db.Refunds.Include(r => r.LedgerEntries).ToListAsync();
        Assert.Single(refunds);
        Assert.Equal(2, refunds[0].LedgerEntries.Count);
    }

    [LocalPostgresPaymentFact]
    public async Task TransientFailure_LeavesPending_ResumesOnNextAttempt()
    {
        await _fixture.ResetTablesAsync();
        var payment = await SeedSettledPaymentAsync();
        const string reason = "Network glitch during refund";

        var transientMockGateway = new Mock<IPaymentGateway>();
        transientMockGateway.Setup(g => g.Method).Returns(PaymentMethod.Card);
        transientMockGateway.Setup(g => g.RefundAsync(It.IsAny<GatewayRefundRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GatewayResult.Transient("network_timeout"));

        // Attempt 1: Transient failure -> leaves Pending in DB
        var (sut1, _) = CreateSut(transientMockGateway.Object);
        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() =>
            sut1.ExecuteAsync(payment.Id, reason, CancellationToken.None));

        await using (var dbCheck = _fixture.CreateDbContext())
        {
            var pendingRefund = await dbCheck.Refunds.Include(r => r.LedgerEntries).SingleAsync();
            Assert.Equal(RefundStatus.Pending, pendingRefund.Status);
            Assert.Null(pendingRefund.SettledAt);
            Assert.Empty(pendingRefund.LedgerEntries); // No ledger entries when pending
        }

        // Attempt 2: Successful gateway -> resumes on same row and settles
        var (sut2, _) = CreateSut(); // standard gateway succeeds
        var result2 = await sut2.ExecuteAsync(payment.Id, reason, CancellationToken.None);

        Assert.False(result2.IsReplay);
        Assert.Equal("Settled", result2.Payment.Refund?.Status);

        await using (var dbCheck = _fixture.CreateDbContext())
        {
            var settledRefund = await dbCheck.Refunds.Include(r => r.LedgerEntries).SingleAsync();
            Assert.Equal(RefundStatus.Settled, settledRefund.Status);
            Assert.NotNull(settledRefund.SettledAt);
            Assert.Equal(2, settledRefund.LedgerEntries.Count);
        }
    }

    [LocalPostgresPaymentFact]
    public async Task DeclinedRefund_CanRetryOnSameRow()
    {
        await _fixture.ResetTablesAsync();
        var payment = await SeedSettledPaymentAsync();
        const string reason = "Retrying after card issuer failure";

        var declinedMockGateway = new Mock<IPaymentGateway>();
        declinedMockGateway.Setup(g => g.Method).Returns(PaymentMethod.Card);
        declinedMockGateway.Setup(g => g.RefundAsync(It.IsAny<GatewayRefundRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GatewayResult.Declined("issuer_rejected"));

        // Attempt 1: Gateway declines -> records Failed in DB
        var (sut1, _) = CreateSut(declinedMockGateway.Object);
        await Assert.ThrowsAsync<InvalidPaymentStateException>(() =>
            sut1.ExecuteAsync(payment.Id, reason, CancellationToken.None));

        await using (var dbCheck = _fixture.CreateDbContext())
        {
            var failedRefund = await dbCheck.Refunds.Include(r => r.LedgerEntries).SingleAsync();
            Assert.Equal(RefundStatus.Failed, failedRefund.Status);
            Assert.Equal("issuer_rejected", failedRefund.FailureCode);
            Assert.Empty(failedRefund.LedgerEntries);
        }

        // Attempt 2: Retry with successful gateway -> resumes same row, transitions to Settled
        var (sut2, _) = CreateSut();
        var result2 = await sut2.ExecuteAsync(payment.Id, reason, CancellationToken.None);

        Assert.False(result2.IsReplay);
        Assert.Equal("Settled", result2.Payment.Refund?.Status);

        await using (var dbCheck = _fixture.CreateDbContext())
        {
            var allRefunds = await dbCheck.Refunds.Include(r => r.LedgerEntries).ToListAsync();
            Assert.Single(allRefunds); // Still exactly one row!
            Assert.Equal(RefundStatus.Settled, allRefunds[0].Status);
            Assert.Equal(2, allRefunds[0].LedgerEntries.Count);
        }
    }
}
