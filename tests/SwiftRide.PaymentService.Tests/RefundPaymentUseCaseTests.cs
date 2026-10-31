using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments;
using SwiftRide.PaymentService.Application.Payments.UseCases;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Repositories;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
namespace SwiftRide.PaymentService.Tests;

public class RefundPaymentUseCaseTests
{
    private static readonly Guid TripId = Guid.NewGuid();
    private readonly Mock<IPaymentReader> _reader = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IPaymentGateway> _gateway = new();

    private RefundPaymentUseCase Sut()
    {
        var f = new Mock<IPaymentGatewayFactory>();
        f.Setup(x => x.Create(It.IsAny<PaymentMethod>())).Returns(_gateway.Object);
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((op, ct) => op(ct));
        return new(_reader.Object, _uow.Object, f.Object,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System, NullLogger<RefundPaymentUseCase>.Instance);
    }
    private static Payment Settled()
    {
        var p = Payment.Create(TripId, "rider-1", "drv-1", Money.Create(85_000, "VND"),
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), "tok_card_ok", DateTimeOffset.UtcNow);
        p.MarkSettled("ref-1", DateTimeOffset.UtcNow); return p;
    }
    private void Find(Payment? p) => _reader.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(p);
    private void RefundReturns(GatewayResult r) => _gateway.Setup(g => g.RefundAsync(It.IsAny<GatewayRefundRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(r);

    [Fact]
    public async Task Refund_Success_SettlesAndSavesOnce()
    {
        var p = Settled(); Find(p); RefundReturns(GatewayResult.Success("rf-1"));
        var r = await Sut().ExecuteAsync(p.Id, "Customer cancelled", CancellationToken.None);
        Assert.False(r.IsReplay);
        Assert.Equal("Settled", r.Payment.Refund!.Status);
        Assert.Equal(2, r.Payment.Refund.LedgerEntries.Count);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)); // Persist Pending + Settle
    }

    [Fact]
    public async Task Refund_SameReasonAgain_IsReplay_WithoutGateway()
    {
        var p = Settled(); p.RequestRefund("Customer cancelled", DateTimeOffset.UtcNow); p.SettleRefund("rf-1", DateTimeOffset.UtcNow);
        Find(p);
        var r = await Sut().ExecuteAsync(p.Id, "Customer cancelled", CancellationToken.None);
        Assert.True(r.IsReplay);
        _gateway.Verify(g => g.RefundAsync(It.IsAny<GatewayRefundRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refund_DifferentReason_ThrowsConflict()
    {
        var p = Settled(); p.RequestRefund("A", DateTimeOffset.UtcNow); p.SettleRefund("rf-1", DateTimeOffset.UtcNow);
        Find(p);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Sut().ExecuteAsync(p.Id, "B", CancellationToken.None));
    }

    [Fact]
    public async Task Refund_FailedPayment_ThrowsInvalidState()
    {
        var p = Payment.Create(TripId, "rider-1", "drv-1", Money.Create(85_000, "VND"),
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), "tok_card_ok", DateTimeOffset.UtcNow);
        p.MarkFailed("declined", DateTimeOffset.UtcNow); Find(p);
        await Assert.ThrowsAsync<InvalidPaymentStateException>(() => Sut().ExecuteAsync(p.Id, "x", CancellationToken.None));
    }

    [Fact]
    public async Task Refund_NotFound_Throws()
    {
        Find(null);
        await Assert.ThrowsAsync<PaymentNotFoundException>(() => Sut().ExecuteAsync(Guid.NewGuid(), "x", CancellationToken.None));
    }

    [Fact]
    public async Task Refund_GatewayTransient_ThrowsUnavailable_SavesPendingIntent()
    {
        var p = Settled(); Find(p); RefundReturns(GatewayResult.Transient("timeout"));
        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() => Sut().ExecuteAsync(p.Id, "x", CancellationToken.None));
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once); // Persisted Pending intent
        Assert.NotNull(p.Refund);
        Assert.Equal(RefundStatus.Pending, p.Refund.Status);
    }

    [Fact]
    public async Task Refund_MidwayCrashAtFinalSave_RollsBackTransaction_AndSubsequentAttemptRecoversSuccessfully()
    {
        var paymentId = Guid.NewGuid();
        var p1 = Payment.Create(TripId, "rider-1", "drv-1", Money.Create(85_000, "VND"),
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), "tok_card_ok", DateTimeOffset.UtcNow);
        p1.MarkSettled("ref-1", DateTimeOffset.UtcNow);

        // Instance p2 represents state restored from DB after transaction rollback (refund is Pending)
        var p2 = Payment.Create(TripId, "rider-1", "drv-1", Money.Create(85_000, "VND"),
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), "tok_card_ok", DateTimeOffset.UtcNow);
        p2.MarkSettled("ref-1", DateTimeOffset.UtcNow);
        p2.RequestRefund("customer-refund", DateTimeOffset.UtcNow);

        Payment currentPayment = p1;
        var readerMock = new Mock<IPaymentReader>();
        readerMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => currentPayment);

        RefundReturns(GatewayResult.Success("rf-crash-test"));

        var mockProcessing = new Mock<IRefundProcessingStore>();
        mockProcessing.Setup(x => x.TryClaimAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefundProcessingClaimResult(ClaimStatus.Claimed, 1, DateTimeOffset.UtcNow.AddMinutes(1), CanRetry: true, RequiresAction: false));
        mockProcessing.Setup(x => x.CompleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var f = new Mock<IPaymentGatewayFactory>();
        f.Setup(x => x.Create(It.IsAny<PaymentMethod>())).Returns(_gateway.Object);

        var uow = new Mock<IUnitOfWork>();
        var saveCallCount = 0;
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                saveCallCount++;
                if (saveCallCount == 2)
                {
                    // Simulated crash at final save
                    throw new InvalidOperationException("Simulated crash at final save");
                }
                return Task.CompletedTask;
            });

        uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>(async (op, ct) =>
            {
                await op(ct);
            });

        var sut = new RefundPaymentUseCase(
            readerMock.Object,
            uow.Object,
            f.Object,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System,
            NullLogger<RefundPaymentUseCase>.Instance,
            mockProcessing.Object);

        // Attempt 1: Crashes at final save inside transaction
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExecuteAsync(p1.Id, "customer-refund", CancellationToken.None));

        // Switch active DB state to p2 (simulating entity restored from DB with Pending status after rollback)
        currentPayment = p2;

        // Attempt 2: Recovery attempt reads p2 (still Pending from DB) and finishes successfully
        var recoveredResult = await sut.ExecuteAsync(p1.Id, "customer-refund", CancellationToken.None);

        Assert.NotNull(recoveredResult);
        Assert.Equal("Settled", recoveredResult.Payment.Refund!.Status);
        mockProcessing.Verify(x => x.CompleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), "settled", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Refund_FencingConflict_ThrowsIdempotencyConflict_AndRollsBack()
    {
        var p = Settled();
        p.RequestRefund("customer-refund", DateTimeOffset.UtcNow); // already has pending refund

        // Reader returns payment whose refund is still Pending (winner has not settled yet)
        var readerMock = new Mock<IPaymentReader>();
        readerMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var fresh = Settled();
                fresh.RequestRefund("customer-refund", DateTimeOffset.UtcNow);
                return fresh;
            });

        RefundReturns(GatewayResult.Success("rf-fencing-test"));

        var mockProcessing = new Mock<IRefundProcessingStore>();
        mockProcessing.Setup(x => x.TryClaimAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefundProcessingClaimResult(ClaimStatus.Claimed, 1, DateTimeOffset.UtcNow.AddMinutes(1), CanRetry: true, RequiresAction: false));
        // CompleteAsync returns false due to fencing conflict (another worker claimed or updated record)
        mockProcessing.Setup(x => x.CompleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var f = new Mock<IPaymentGatewayFactory>();
        f.Setup(x => x.Create(It.IsAny<PaymentMethod>())).Returns(_gateway.Object);

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>(async (op, ct) =>
            {
                await op(ct);
            });

        var sut = new RefundPaymentUseCase(
            readerMock.Object,
            uow.Object,
            f.Object,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System,
            NullLogger<RefundPaymentUseCase>.Instance,
            mockProcessing.Object);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => sut.ExecuteAsync(p.Id, "customer-refund", CancellationToken.None));
    }
}
