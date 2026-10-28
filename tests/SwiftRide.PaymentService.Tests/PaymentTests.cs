using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
namespace SwiftRide.PaymentService.Tests;

public class PaymentTests
{
    private static readonly Guid TripId = Guid.NewGuid();
    private const string RiderId = "rider-1", DriverId = "drv-1";
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
    public void Refund_AppendsReverseEntries_AndLeavesPaymentSettled()
    {
        var p = NewPayment(); p.MarkSettled("ref-1", DateTimeOffset.UtcNow);
        p.RequestRefund("rider cancelled", DateTimeOffset.UtcNow);
        p.SettleRefund("rf-1", DateTimeOffset.UtcNow);

        Assert.Equal(PaymentStatus.Settled, p.Status);        // payment không đổi trạng thái
        Assert.Equal(2, p.LedgerEntries.Count);               // sổ cái gốc giữ nguyên
        Assert.NotNull(p.Refund);
        Assert.Equal(RefundStatus.Settled, p.Refund!.Status);
        Assert.Equal(2, p.Refund.LedgerEntries.Count);        // thêm 2 dòng đảo ở bảng refund
        Assert.Equal(
            p.Refund.LedgerEntries.Where(e => e.Direction == EntryDirection.Debit).Sum(e => e.Amount.Amount),
            p.Refund.LedgerEntries.Where(e => e.Direction == EntryDirection.Credit).Sum(e => e.Amount.Amount));
    }

    [Fact]
    public void Refund_Twice_Throws()
    {
        var p = NewPayment(); p.MarkSettled("ref-1", DateTimeOffset.UtcNow);
        p.RequestRefund("first", DateTimeOffset.UtcNow);
        Assert.ThrowsAny<DomainException>(() => p.RequestRefund("second", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Refund_BeforeSettled_Throws() =>
    Assert.Throws<InvalidPaymentStateException>(() => NewPayment().RequestRefund("x", DateTimeOffset.UtcNow));

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