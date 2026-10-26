using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
namespace SwiftRide.PaymentService.Tests;

public class PaymentTests
{
    private static readonly Guid TripId = Guid.NewGuid(), RiderId = Guid.NewGuid(), DriverId = Guid.NewGuid();
    private static Payment NewPayment() => Payment.Create(TripId, RiderId, DriverId,
        Money.Create(85_000, "VND"), PaymentMethod.Card, PaymentIdempotency.KeyForTrip(TripId), DateTimeOffset.UtcNow);

    [Fact]
    public void MarkSettled_PostsBalancedLedger()
    {
        var p = NewPayment(); p.MarkSettled("ref-1", DateTimeOffset.UtcNow);
        Assert.Equal(PaymentStatus.Settled, p.Status);
        Assert.Equal(2, p.LedgerEntries.Count);
        Assert.True(p.IsLedgerBalanced());
    }

    [Fact]
    public void MarkFailed_PostsNoLedger()
    {
        var p = NewPayment(); p.MarkFailed("declined", DateTimeOffset.UtcNow);
        Assert.Equal(PaymentStatus.Failed, p.Status);
        Assert.Empty(p.LedgerEntries);
    }

    [Fact]
    public void Refund_AppendsReverseEntries_AndStaysBalanced()
    {
        var p = NewPayment(); p.MarkSettled("ref-1", DateTimeOffset.UtcNow);
        p.Refund("rider cancelled", DateTimeOffset.UtcNow);
        Assert.Equal(PaymentStatus.Refunded, p.Status);
        Assert.Equal(4, p.LedgerEntries.Count);       // dòng cũ giữ nguyên, chỉ thêm 2 dòng đảo
        Assert.True(p.IsLedgerBalanced());
    }

    [Fact]
    public void Refund_BeforeSettled_Throws() =>
        Assert.Throws<InvalidPaymentStateException>(() => NewPayment().Refund("x", DateTimeOffset.UtcNow));

    [Fact]
    public void Create_WithKeyOfAnotherTrip_Throws() =>
        Assert.Throws<DomainException>(() => Payment.Create(TripId, RiderId, DriverId,
            Money.Create(85_000, "VND"), PaymentMethod.Card, PaymentIdempotency.KeyForTrip(Guid.NewGuid()), DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.5)]
    public void Money_InvalidAmount_Throws(decimal amount) =>
        Assert.Throws<DomainException>(() => Money.Create(amount, "VND"));
}