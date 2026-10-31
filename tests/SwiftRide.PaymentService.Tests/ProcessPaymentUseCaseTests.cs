using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Application.Payments.UseCases;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Repositories;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
namespace SwiftRide.PaymentService.Tests;

public class ProcessPaymentUseCaseTests
{
    private static readonly Guid TripId = Guid.NewGuid();
    private const string RiderId = "rider-1", DriverId = "drv-1";
    private readonly Mock<IPaymentReader> _reader = new();
    private readonly Mock<IPaymentWriter> _writer = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IPaymentGateway> _gateway = new();
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }

    private ProcessPaymentUseCase Sut(TimeProvider? clock = null, IPaymentMetrics? metrics = null)
    {
        var factory = new Mock<IPaymentGatewayFactory>();
        factory.Setup(f => f.Create(It.IsAny<PaymentMethod>())).Returns(_gateway.Object);
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((op, ct) => op(ct));
        return new ProcessPaymentUseCase(_reader.Object, _writer.Object, _uow.Object, factory.Object,
            new GatewayCallPolicy(new PaymentGatewaySettings(TimeSpan.FromSeconds(5))),
            clock ?? TimeProvider.System, NullLogger<ProcessPaymentUseCase>.Instance,
            processingStore: null,
            metrics: metrics);
    }

    private static ProcessPaymentInput Input(
        decimal amount = 85_000,
        string? riderId = RiderId,
        string? driverId = DriverId,
        PaymentMethod method = PaymentMethod.Card,
        string token = "tok_card_ok",
        string? currency = "VND") => new(TripId, riderId, driverId,
        amount, currency, method, token, PaymentIdempotency.KeyForTrip(TripId));

    private static Payment Settled(decimal amount = 85_000, string token = "tok_card_ok")
    {
        var p = Payment.Create(TripId, RiderId, DriverId, Money.Create(amount, "VND"),
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), token, DateTimeOffset.UtcNow);
        p.MarkSettled("ref-1", DateTimeOffset.UtcNow);
        return p;
    }

    private void NoExistingPayment() => _reader.Setup(r =>
        r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Payment?)null);

    private void GatewayReturns(GatewayResult result) => _gateway.Setup(g =>
        g.ChargeAsync(It.IsAny<GatewayChargeRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);

    [Fact]
    public async Task NewPayment_IsSettled_AndSavedOnce()
    {
        NoExistingPayment(); GatewayReturns(GatewayResult.Success("ref-1"));
        var result = await Sut().ExecuteAsync(Input(), CancellationToken.None);
        Assert.False(result.IsReplay);
        Assert.Equal("Settled", result.Payment.Status);
        Assert.Equal(2, result.Payment.LedgerEntries.Count);
        _writer.Verify(w => w.Add(It.IsAny<Payment>()), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)); // Pending rồi Settled
    }

    [Fact]
    public async Task ZeroFare_SettlesAutomatically_WithoutCallingGateway()
    {
        NoExistingPayment();
        var result = await Sut().ExecuteAsync(Input(amount: 0m), CancellationToken.None);

        Assert.False(result.IsReplay);
        Assert.Equal("Settled", result.Payment.Status);
        Assert.Equal(0m, result.Payment.Amount);
        Assert.Equal(2, result.Payment.LedgerEntries.Count);
        Assert.True(result.Payment.LedgerEntries.All(e => e.Amount == 0m));

        _gateway.Verify(g => g.ChargeAsync(It.IsAny<GatewayChargeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _writer.Verify(w => w.Add(It.IsAny<Payment>()), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)); // Pending rồi Settled
    }

    [Fact]
    public async Task SameKeySamePayload_ReturnsOriginal_WithoutChargingAgain()
    {
        var existing = Settled();
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        var result = await Sut().ExecuteAsync(Input(), CancellationToken.None);
        Assert.True(result.IsReplay);
        Assert.Equal(existing.Id, result.Payment.Id);
        _gateway.Verify(g => g.ChargeAsync(It.IsAny<GatewayChargeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _writer.Verify(w => w.Add(It.IsAny<Payment>()), Times.Never);
    }

    [Fact]
    public async Task SameKeyDifferentAmount_ThrowsConflict()
    {
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Settled());
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Sut().ExecuteAsync(Input(90_000), CancellationToken.None));
    }

    [Fact]
    public async Task SameKeyDifferentToken_ThrowsConflict()
    {
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Settled(token: "tok_card_ok"));
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Sut().ExecuteAsync(Input(token: "tok_card_other"), CancellationToken.None));
    }

    [Fact]
    public async Task SameKeyDifferentRider_ThrowsConflict()
    {
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Settled());
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Sut().ExecuteAsync(Input(riderId: "rider-other"), CancellationToken.None));
    }

    [Fact]
    public async Task SameKeyDifferentDriver_ThrowsConflict()
    {
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Settled());
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Sut().ExecuteAsync(Input(driverId: "drv-other"), CancellationToken.None));
    }

    [Fact]
    public async Task SameKeyDifferentMethod_ThrowsConflict()
    {
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Settled());
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Sut().ExecuteAsync(Input(method: PaymentMethod.Wallet), CancellationToken.None));
    }

    [Fact]
    public async Task DeclinedCard_SavesFailedPayment_WithoutLedger()
    {
        NoExistingPayment(); GatewayReturns(GatewayResult.Declined("declined"));
        var result = await Sut().ExecuteAsync(Input(), CancellationToken.None);
        Assert.Equal("Failed", result.Payment.Status);
        Assert.Empty(result.Payment.LedgerEntries);
        _writer.Verify(w => w.Add(It.IsAny<Payment>()), Times.Once);
    }

    [Fact]
    public async Task TransientFailure_ThrowsUnavailable_AndPersistsNothing()
    {
        NoExistingPayment(); GatewayReturns(GatewayResult.Transient("timeout"));
        await Assert.ThrowsAsync<PaymentGatewayUnavailableException>(() => Sut().ExecuteAsync(Input(), CancellationToken.None));
        _writer.Verify(w => w.Add(It.IsAny<Payment>()), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once); // chỉ lần INSERT Pending
    }

    private static Payment PendingAt(DateTimeOffset t, string token = "tok_card_ok") => Payment.Create(TripId, RiderId, DriverId,
        Money.Create(85_000, "VND"), PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), token, t);

    [Fact]
    public async Task PendingOlderThanLease_ResumesSamePayment()
    {
        var t0 = DateTimeOffset.UtcNow; var pending = PendingAt(t0);
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(pending);
        GatewayReturns(GatewayResult.Success("ref-1"));
        var r = await Sut(new FakeClock(t0.AddSeconds(31))).ExecuteAsync(Input(), CancellationToken.None);
        Assert.Equal(pending.Id, r.Payment.Id);
        Assert.Equal("Settled", r.Payment.Status);
        _writer.Verify(w => w.Add(It.IsAny<Payment>()), Times.Never);
    }

    [Fact]
    public async Task PendingWithinLease_ReturnsPending_WithoutCallingGateway()
    {
        var t0 = DateTimeOffset.UtcNow;
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(PendingAt(t0));
        var r = await Sut(new FakeClock(t0.AddSeconds(5))).ExecuteAsync(Input(), CancellationToken.None);
        Assert.Equal("Pending", r.Payment.Status);
        _gateway.Verify(g => g.ChargeAsync(It.IsAny<GatewayChargeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentDuplicate_ReturnsTheWinnerPayment()
    {
        var winner = Settled();
        _reader.SetupSequence(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null).ReturnsAsync(winner);
        GatewayReturns(GatewayResult.Success("ref-2"));
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new DuplicateIdempotencyKeyException());
        var result = await Sut().ExecuteAsync(Input(), CancellationToken.None);
        Assert.True(result.IsReplay);
        Assert.Equal(winner.Id, result.Payment.Id);
    }

    [Fact]
    public async Task ProcessPayment_IncrementsMetricsOnSettledAndFailed_AndIgnoresReplay()
    {
        var mockMetrics = new Mock<IPaymentMetrics>();

        // 1. New settled payment -> PaymentSettled called once
        NoExistingPayment();
        GatewayReturns(GatewayResult.Success("ref-metric-1"));
        var sut = Sut(metrics: mockMetrics.Object);

        var r1 = await sut.ExecuteAsync(Input(), CancellationToken.None);
        Assert.False(r1.IsReplay);
        mockMetrics.Verify(m => m.PaymentSettled(), Times.Once);
        mockMetrics.Verify(m => m.PaymentFailed(), Times.Never);

        // 2. Replay settled payment -> PaymentSettled NOT called again
        var settledPayment = Settled();
        _reader.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(settledPayment);

        var r2 = await sut.ExecuteAsync(Input(), CancellationToken.None);
        Assert.True(r2.IsReplay);
        mockMetrics.Verify(m => m.PaymentSettled(), Times.Once); // Still once!
        mockMetrics.Verify(m => m.PaymentFailed(), Times.Never);

        // 3. New failed payment -> PaymentFailed called once
        NoExistingPayment();
        GatewayReturns(GatewayResult.Declined("insufficient_funds"));

        var r3 = await sut.ExecuteAsync(Input(amount: 90_000), CancellationToken.None);
        Assert.False(r3.IsReplay);
        mockMetrics.Verify(m => m.PaymentSettled(), Times.Once);
        mockMetrics.Verify(m => m.PaymentFailed(), Times.Once);

        // 4. Zero fare payment auto-settled -> PaymentSettled called again (total 2)
        NoExistingPayment();
        var zeroInput = Input(amount: 0m);
        var r4 = await sut.ExecuteAsync(zeroInput, CancellationToken.None);
        Assert.False(r4.IsReplay);
        mockMetrics.Verify(m => m.PaymentSettled(), Times.Exactly(2));
    }
}
