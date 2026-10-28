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
        return new(_reader.Object, _uow.Object, f.Object,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            TimeProvider.System, NullLogger<RefundPaymentUseCase>.Instance);
    }
    private static Payment Settled()
    {
        var p = Payment.Create(TripId, "rider-1", "drv-1", Money.Create(85_000, "VND"),
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), DateTimeOffset.UtcNow);
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
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
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
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), DateTimeOffset.UtcNow);
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
    public async Task Refund_GatewayTransient_ThrowsUnavailable_SavesNothing()
    {
        var p = Settled(); Find(p); RefundReturns(GatewayResult.Transient("timeout"));
        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() => Sut().ExecuteAsync(p.Id, "x", CancellationToken.None));
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}