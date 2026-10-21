using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Tests.Domain;

public class TripPaymentTests
{
    [Fact]
    public void RequestPayment_UsesStableTripPaymentKey()
    {
        var trip = TripScenario.PickedUp();
        trip.Dropoff("driver-1", TripScenario.DropoffUtc);

        trip.RequestPayment();

        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.False(trip.IsTerminal);
        Assert.Equal(
            "trip-11111111-1111-1111-1111-111111111111-payment",
            trip.PaymentIdempotencyKey);
        Assert.Equal(74_844m, trip.Fare!.Amount);
    }

    [Theory]
    [InlineData(PaymentFailureKind.Transient)]
    [InlineData(PaymentFailureKind.UnknownResult)]
    [InlineData(PaymentFailureKind.Permanent)]
    [InlineData(PaymentFailureKind.RetryLimitReached)]
    public void PaymentFailure_RemainsPendingWithoutChangingKeyOrFare(PaymentFailureKind failureKind)
    {
        var trip = TripScenario.PendingPayment();
        var originalKey = trip.PaymentIdempotencyKey;
        var originalFare = trip.Fare;

        trip.RecordPaymentFailure(failureKind);

        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.False(trip.IsTerminal);
        Assert.Equal(failureKind, trip.LastPaymentFailure);
        Assert.Null(trip.PaymentId);
        Assert.Equal(originalKey, trip.PaymentIdempotencyKey);
        Assert.Same(originalFare, trip.Fare);
    }

    [Fact]
    public void SettlePayment_MatchingResultMarksPaid()
    {
        var trip = TripScenario.PendingPayment();
        trip.RecordPaymentFailure(PaymentFailureKind.Transient);

        var settlement = new PaymentSettlement(
            TripScenario.PaymentId,
            TripScenario.TripId,
            TripScenario.Fare,
            "VND",
            trip.PaymentIdempotencyKey!);

        trip.SettlePayment(settlement);

        Assert.Equal(TripStatus.Paid, trip.Status);
        Assert.True(trip.IsTerminal);
        Assert.Equal(TripScenario.PaymentId, trip.PaymentId);
        Assert.Null(trip.LastPaymentFailure);
    }

    [Fact]
    public void SettlePayment_ZeroAmount_MarksPaid()
    {
        var trip = Trip.Request(TripScenario.TripId, "rider-1");
        trip.ApplyQuote(new FareSnapshot(TripScenario.QuoteId, 0m, "VND"), "driver-1");
        trip.ConfirmQuote("rider-1", TripScenario.QuoteId, "driver-1");
        trip.AcceptDriver("driver-1");
        trip.StartEnRoute("driver-1");
        trip.Pickup("driver-1");
        trip.Dropoff("driver-1", TripScenario.DropoffUtc);
        trip.RequestPayment();

        var settlement = new PaymentSettlement(
            TripScenario.PaymentId,
            TripScenario.TripId,
            0m,
            "VND",
            trip.PaymentIdempotencyKey!);

        trip.SettlePayment(settlement);

        Assert.Equal(TripStatus.Paid, trip.Status);
        Assert.Equal(0m, trip.Fare!.Amount);
    }

    [Theory]
    [InlineData("wrong-trip-id")]
    [InlineData("wrong-amount")]
    [InlineData("wrong-currency")]
    [InlineData("wrong-key")]
    public void SettlePayment_RejectsMismatchedResultWithoutMutation(string scenario)
    {
        var trip = TripScenario.PendingPayment();

        var settlement = scenario switch
        {
            "wrong-trip-id" => new PaymentSettlement(
                TripScenario.PaymentId, Guid.NewGuid(), TripScenario.Fare, "VND", trip.PaymentIdempotencyKey!),
            "wrong-amount" => new PaymentSettlement(
                TripScenario.PaymentId, TripScenario.TripId, 74_845m, "VND", trip.PaymentIdempotencyKey!),
            "wrong-currency" => new PaymentSettlement(
                TripScenario.PaymentId, TripScenario.TripId, TripScenario.Fare, "USD", trip.PaymentIdempotencyKey!),
            "wrong-key" => new PaymentSettlement(
                TripScenario.PaymentId, TripScenario.TripId, TripScenario.Fare, "VND", "different-idempotency-key"),
            _ => throw new ArgumentException()
        };

        var ex = Assert.Throws<TripDomainException>(() => trip.SettlePayment(settlement));
        Assert.Equal("PaymentMismatch", ex.Code);
        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.Null(trip.PaymentId);
        Assert.False(trip.IsTerminal);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "11111111-1111-1111-1111-111111111111", "74844", "VND", "key-1")]
    [InlineData("33333333-3333-3333-3333-333333333333", "00000000-0000-0000-0000-000000000000", "74844", "VND", "key-1")]
    [InlineData("33333333-3333-3333-3333-333333333333", "11111111-1111-1111-1111-111111111111", "-1", "VND", "key-1")]
    [InlineData("33333333-3333-3333-3333-333333333333", "11111111-1111-1111-1111-111111111111", "1.5", "VND", "key-1")]
    [InlineData("33333333-3333-3333-3333-333333333333", "11111111-1111-1111-1111-111111111111", "74844", "", "key-1")]
    [InlineData("33333333-3333-3333-3333-333333333333", "11111111-1111-1111-1111-111111111111", "74844", "VND", "")]
    [InlineData("33333333-3333-3333-3333-333333333333", "11111111-1111-1111-1111-111111111111", "74844", "VND", "   ")]
    public void PaymentSettlement_RejectsMalformedResult(string paymentIdStr, string tripIdStr, string amountStr, string currency, string key)
    {
        var paymentId = Guid.Parse(paymentIdStr);
        var tripId = Guid.Parse(tripIdStr);
        var amount = decimal.Parse(amountStr, System.Globalization.CultureInfo.InvariantCulture);

        var ex = Assert.Throws<TripDomainException>(() => new PaymentSettlement(paymentId, tripId, amount, currency, key));
        Assert.Equal("InvalidPaymentResult", ex.Code);
    }

    [Fact]
    public void Settlement_AfterRetryExhaustion_CanStillReconcile()
    {
        var trip = TripScenario.PendingPayment();
        trip.RecordPaymentFailure(PaymentFailureKind.RetryLimitReached);
        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.False(trip.IsTerminal);

        var settlement = new PaymentSettlement(
            TripScenario.PaymentId,
            TripScenario.TripId,
            TripScenario.Fare,
            "VND",
            trip.PaymentIdempotencyKey!);

        trip.SettlePayment(settlement);

        Assert.Equal(TripStatus.Paid, trip.Status);
        Assert.True(trip.IsTerminal);
        Assert.Equal(TripScenario.PaymentId, trip.PaymentId);
    }

    [Fact]
    public void Paid_RejectsRepeatedSettlementAtDomainBoundary()
    {
        var trip = TripScenario.PendingPayment();
        var settlement = new PaymentSettlement(
            TripScenario.PaymentId,
            TripScenario.TripId,
            TripScenario.Fare,
            "VND",
            trip.PaymentIdempotencyKey!);

        trip.SettlePayment(settlement);
        Assert.Equal(TripStatus.Paid, trip.Status);

        var ex = Assert.Throws<TripDomainException>(() => trip.SettlePayment(settlement));
        Assert.Equal("InvalidTransition", ex.Code);
        Assert.Equal(TripScenario.PaymentId, trip.PaymentId);
    }
}
