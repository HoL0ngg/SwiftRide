using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Payments;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Application.Payments.UseCases;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
using SwiftRide.PaymentService.Infrastructure.Gateways;
using SwiftRide.PaymentService.Infrastructure.Persistence;
using SwiftRide.PaymentService.Tests.Infrastructure;
using Xunit;

namespace SwiftRide.PaymentService.Tests;

[Collection("PaymentDatabase")]
public class PaymentRecoveryTests
{
    private readonly PaymentDatabaseFixture _fixture;

    public PaymentRecoveryTests(PaymentDatabaseFixture fixture)
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

    private (ProcessPaymentUseCase useCase, MockOperationStore mockStore) CreateSut()
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

        return (useCase, mockStore);
    }

    [LocalPostgresPaymentFact]
    public async Task RestartAfterGatewaySuccess_FinalizesOnce()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

        var input = new ProcessPaymentInput(
            tripId,
            "rider-1",
            "driver-1",
            75_000m,
            "VND",
            PaymentMethod.Card,
            "tok_card_ok",
            idempotencyKey);

        // Host 1 executes payment
        var (sut1, _) = CreateSut();
        var result1 = await sut1.ExecuteAsync(input, CancellationToken.None);

        Assert.False(result1.IsReplay);
        Assert.Equal("Settled", result1.Payment.Status);

        // Host restarts (Host 2 with fresh context on same DB)
        var (sut2, _) = CreateSut();
        var result2 = await sut2.ExecuteAsync(input, CancellationToken.None);

        Assert.True(result2.IsReplay);
        Assert.Equal(result1.Payment.Id, result2.Payment.Id);
        Assert.Equal("Settled", result2.Payment.Status);

        // Verify database state: exactly 1 payment, 2 ledger entries, 1 mock operation
        await using var db = _fixture.CreateDbContext();
        var payments = await db.Payments.Include(p => p.LedgerEntries).ToListAsync();
        var mockOps = await db.MockGatewayOperations.ToListAsync();

        Assert.Single(payments);
        Assert.Equal(2, payments[0].LedgerEntries.Count);
        Assert.Single(mockOps);
        Assert.Equal("succeeded", mockOps[0].Outcome);
    }

    [LocalPostgresPaymentFact]
    public async Task ConcurrentRequests_SameKey_OneWinnerAndReplays()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

        var input = new ProcessPaymentInput(
            tripId,
            "rider-concurrent",
            "driver-concurrent",
            120_000m,
            "VND",
            PaymentMethod.Card,
            "tok_card_ok",
            idempotencyKey);

        // 20 concurrent requests
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var (sut, _) = CreateSut();
            return await sut.ExecuteAsync(input, CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);

        // 1. All 20 responses received
        Assert.Equal(20, results.Length);

        // 2. All responses carry the exact same PaymentId
        var firstId = results[0].Payment.Id;
        Assert.All(results, r => Assert.Equal(firstId, r.Payment.Id));

        // 3. Status is strictly Pending or Settled across all responses
        Assert.All(results, r => Assert.Contains(r.Payment.Status, new[] { "Pending", "Settled" }));

        // 4. At least one request finished with Settled
        Assert.Contains(results, r => r.Payment.Status == "Settled");

        // 5. Exactly one winner charged the gateway and settled (IsReplay == false && Settled)
        Assert.Single(results, r => !r.IsReplay && r.Payment.Status == "Settled");

        // 6. In-flight duplicates received either Pending or Settled replay.
        // If creator thread (existing == null) lost processing claim to another thread, creator returns
        // (IsReplay: false, Pending) while processing winner returns (IsReplay: false, Settled).
        // If creator won processing claim, only 1 request has IsReplay: false.
        // Therefore, non-replay count is in [1, 2] and replay count is in [18, 19].
        var nonReplays = results.Where(r => !r.IsReplay).ToList();
        var replays = results.Where(r => r.IsReplay).ToList();
        Assert.InRange(nonReplays.Count, 1, 2);
        Assert.InRange(replays.Count, 18, 19);
        Assert.Equal(20, nonReplays.Count + replays.Count);

        // Any non-replay that is not the Settled winner must be the creator returning in-flight Pending
        Assert.All(nonReplays, r => Assert.Contains(r.Payment.Status, new[] { "Pending", "Settled" }));
        Assert.True(nonReplays.Count(r => r.Payment.Status == "Pending") <= 1);

        // 7. Subsequent request after completion returns Settled replay
        var (afterSut, _) = CreateSut();
        var replayAfter = await afterSut.ExecuteAsync(input, CancellationToken.None);
        Assert.True(replayAfter.IsReplay);
        Assert.Equal("Settled", replayAfter.Payment.Status);
        Assert.Equal(firstId, replayAfter.Payment.Id);

        // 8. DB assertions: exactly 1 payment in DB, with 2 balanced ledger entries, attemptsStarted == 1, exactly 1 gateway charge
        await using var db = _fixture.CreateDbContext();
        var payments = await db.Payments.Include(p => p.LedgerEntries).ToListAsync();
        var mockOps = await db.MockGatewayOperations.ToListAsync();
        var processings = await db.PaymentProcessings.ToListAsync();

        // Exactly 1 payment in DB
        Assert.Single(payments);
        Assert.Equal(firstId, payments[0].Id);
        Assert.Equal(PaymentStatus.Settled, payments[0].Status);

        // Exactly 1 mock gateway operation with "succeeded" outcome (no double charge!)
        Assert.Single(mockOps);
        Assert.Equal("succeeded", mockOps[0].Outcome);

        // Processing record has attemptsStarted == 1 for non-transient success and outcome "settled"
        Assert.Single(processings);
        Assert.Equal(firstId, processings[0].PaymentId);
        Assert.Equal(1, processings[0].AttemptsStarted);
        Assert.Equal("settled", processings[0].Outcome);

        // Exactly 2 balanced ledger entries (1 Debit and 1 Credit with equal amounts)
        Assert.Equal(2, payments[0].LedgerEntries.Count);
        var debit = payments[0].LedgerEntries.Single(e => e.Direction == EntryDirection.Debit);
        var credit = payments[0].LedgerEntries.Single(e => e.Direction == EntryDirection.Credit);
        Assert.Equal(120_000m, debit.Amount.Amount);
        Assert.Equal(120_000m, credit.Amount.Amount);
        Assert.Equal(debit.Amount.Currency, credit.Amount.Currency);
    }

    [LocalPostgresPaymentFact]
    public async Task ClaimLease_CompetingOwner_GetsCurrentlyLeased_UntilExpiry()
    {
        await _fixture.ResetTablesAsync();
        await using var db = _fixture.CreateDbContext();
        var store = new PaymentProcessingStore(db);

        var tripId = Guid.NewGuid();
        var payment = Payment.Create(tripId, "r1", "d1", Money.Create(10_000m, "VND"), PaymentMethod.Card,
            PaymentIdempotency.KeyForTrip(tripId), "tok_card_ok", DateTimeOffset.UtcNow);
        db.Payments.Add(payment);

        var paymentId = payment.Id;
        var now = DateTimeOffset.UtcNow;
        await store.InsertAsync(paymentId, "hash1", "drv1", now, CancellationToken.None);
        await db.SaveChangesAsync();

        var owner1 = Guid.NewGuid();
        var owner2 = Guid.NewGuid();

        // Owner 1 claims lease for 10 seconds
        var claim1 = await store.TryClaimAsync(paymentId, owner1, now, TimeSpan.FromSeconds(10), maxAttempts: 3, CancellationToken.None);
        Assert.Equal(ClaimStatus.Claimed, claim1.Status);
        Assert.Equal(1, claim1.AttemptsStarted);

        // Owner 2 tries to claim while lease is active
        var claim2 = await store.TryClaimAsync(paymentId, owner2, now.AddSeconds(2), TimeSpan.FromSeconds(10), maxAttempts: 3, CancellationToken.None);
        Assert.Equal(ClaimStatus.CurrentlyLeased, claim2.Status);

        // After lease expiry (11 seconds later), Owner 2 can claim
        var claim3 = await store.TryClaimAsync(paymentId, owner2, now.AddSeconds(11), TimeSpan.FromSeconds(10), maxAttempts: 3, CancellationToken.None);
        Assert.Equal(ClaimStatus.Claimed, claim3.Status);
        Assert.Equal(2, claim3.AttemptsStarted);
    }

    [LocalPostgresPaymentFact]
    public async Task MaxAttempts_Exceeded_StopsAndRequiresAction()
    {
        await _fixture.ResetTablesAsync();
        await using var db = _fixture.CreateDbContext();
        var store = new PaymentProcessingStore(db);

        var tripId = Guid.NewGuid();
        var payment = Payment.Create(tripId, "r1", "d1", Money.Create(10_000m, "VND"), PaymentMethod.Card,
            PaymentIdempotency.KeyForTrip(tripId), "tok_card_ok", DateTimeOffset.UtcNow);
        db.Payments.Add(payment);

        var paymentId = payment.Id;
        var now = DateTimeOffset.UtcNow;
        await store.InsertAsync(paymentId, "hash1", "drv1", now, CancellationToken.None);
        await db.SaveChangesAsync();

        var owner = Guid.NewGuid();

        // 3 claims (exhausting max attempts = 3)
        var c1 = await store.TryClaimAsync(paymentId, owner, now, TimeSpan.FromSeconds(1), maxAttempts: 3, CancellationToken.None);
        Assert.Equal(ClaimStatus.Claimed, c1.Status);

        var c2 = await store.TryClaimAsync(paymentId, owner, now.AddSeconds(2), TimeSpan.FromSeconds(1), maxAttempts: 3, CancellationToken.None);
        Assert.Equal(ClaimStatus.Claimed, c2.Status);

        var c3 = await store.TryClaimAsync(paymentId, owner, now.AddSeconds(4), TimeSpan.FromSeconds(1), maxAttempts: 3, CancellationToken.None);
        Assert.Equal(ClaimStatus.Claimed, c3.Status);

        // 4th claim attempt -> MaxAttemptsExceeded
        var c4 = await store.TryClaimAsync(paymentId, owner, now.AddSeconds(6), TimeSpan.FromSeconds(1), maxAttempts: 3, CancellationToken.None);
        Assert.Equal(ClaimStatus.MaxAttemptsExceeded, c4.Status);
        Assert.False(c4.CanRetry);
        Assert.True(c4.RequiresAction);
    }

    private sealed class CrashingUnitOfWork(PaymentDbContext db, int crashOnCallNumber) : IUnitOfWork
    {
        private int _callCount;
        private readonly PaymentUnitOfWork _inner = new(db);

        public Task SaveChangesAsync(CancellationToken ct)
        {
            _callCount++;
            if (_callCount == crashOnCallNumber)
            {
                throw new InvalidOperationException($"Simulated crash on SaveChanges call {_callCount}");
            }
            return _inner.SaveChangesAsync(ct);
        }

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
        {
            return _inner.ExecuteInTransactionAsync(operation, ct);
        }
    }

    [LocalPostgresPaymentFact]
    public async Task Refund_MidwayCrashAtFinalSave_AtomicRollback_AndSubsequentAttemptRecovers()
    {
        await _fixture.ResetTablesAsync();
        var tripId = Guid.NewGuid();
        var idempotencyKey = PaymentIdempotency.KeyForTrip(tripId);

        // 1. Setup settled payment
        var processInput = new ProcessPaymentInput(
            tripId,
            "rider-refund-crash",
            "driver-refund-crash",
            100_000m,
            "VND",
            PaymentMethod.Card,
            "tok_card_ok",
            idempotencyKey);

        var (paySut, _) = CreateSut();
        var payResult = await paySut.ExecuteAsync(processInput, CancellationToken.None);
        Assert.Equal("Settled", payResult.Payment.Status);

        var paymentId = payResult.Payment.Id;

        // 2. Setup SUT for refund with simulated crash at final save
        var factory = new TestContextFactory(_fixture.ConnectionString!);
        var mockStore = new MockOperationStore(factory);
        var cardGateway = new MockCardGateway(mockStore);
        var walletGateway = new MockWalletGateway(mockStore);
        var gatewayFactory = new PaymentGatewayFactory(new IPaymentGateway[] { cardGateway, walletGateway });

        // First attempt uses a crashing UnitOfWork at final save
        var dbCrash = _fixture.CreateDbContext();
        var readerCrash = new PaymentRepository(dbCrash);
        var processingCrash = new RefundProcessingStore(dbCrash);
        var crashingUow = new CrashingUnitOfWork(dbCrash, crashOnCallNumber: 2); // Call 1 = persist pending, Call 2 = final save in tx

        var crashSut = new RefundPaymentUseCase(
            readerCrash,
            crashingUow,
            gatewayFactory,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System,
            NullLogger<RefundPaymentUseCase>.Instance,
            processingCrash);

        // Attempt 1 crashes at final save
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashSut.ExecuteAsync(paymentId, "rider-cancelled-trip", CancellationToken.None));

        // 3. Verify database state after rollback:
        // - refund in DB is strictly Pending (not Settled!)
        // - refund_processing in DB is strictly "pending" (not "settled"!)
        await using var dbCheck = _fixture.CreateDbContext();
        var paymentAfterCrash = await dbCheck.Payments.Include(p => p.Refund).FirstOrDefaultAsync(p => p.Id == paymentId);
        Assert.NotNull(paymentAfterCrash);
        Assert.NotNull(paymentAfterCrash.Refund);
        Assert.Equal(RefundStatus.Pending, paymentAfterCrash.Refund.Status);

        var processingAfterCrash = await dbCheck.RefundProcessings.FirstOrDefaultAsync(r => r.PaymentId == paymentId);
        Assert.NotNull(processingAfterCrash);
        Assert.Equal("pending", processingAfterCrash.Outcome);

        // 4. Simulate lease expiry so recovery host can acquire claim
        await using (var dbLease = _fixture.CreateDbContext())
        {
            await dbLease.RefundProcessings
                .Where(r => r.PaymentId == paymentId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        // 5. Recovery attempt with normal context on fresh host
        var dbRecover = _fixture.CreateDbContext();
        var readerRecover = new PaymentRepository(dbRecover);
        var uowRecover = new PaymentUnitOfWork(dbRecover);
        var processingRecover = new RefundProcessingStore(dbRecover);

        var recoverSut = new RefundPaymentUseCase(
            readerRecover,
            uowRecover,
            gatewayFactory,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System,
            NullLogger<RefundPaymentUseCase>.Instance,
            processingRecover);

        var recoveredResult = await recoverSut.ExecuteAsync(paymentId, "rider-cancelled-trip", CancellationToken.None);

        Assert.NotNull(recoveredResult);
        Assert.Equal("Settled", recoveredResult.Payment.Refund!.Status);

        // 6. Final DB verification:
        await using var dbFinal = _fixture.CreateDbContext();
        var finalPayment = await dbFinal.Payments.Include(p => p.Refund).ThenInclude(r => r!.LedgerEntries).FirstOrDefaultAsync(p => p.Id == paymentId);
        Assert.NotNull(finalPayment?.Refund);
        Assert.Equal(RefundStatus.Settled, finalPayment.Refund.Status);
        Assert.Equal(2, finalPayment.Refund.LedgerEntries.Count);

        var finalProcessing = await dbFinal.RefundProcessings.FirstOrDefaultAsync(r => r.PaymentId == paymentId);
        Assert.NotNull(finalProcessing);
        Assert.Equal("settled", finalProcessing.Outcome);
    }
}
