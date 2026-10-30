using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
using Xunit;

namespace SwiftRide.PaymentService.Tests;

public class PaymentFingerprintTests
{
    private static readonly Guid TripId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string RiderId = "rider-123";
    private const string DriverId = "driver-456";
    private static readonly Money Amount = Money.Create(100_000m, "VND");
    private const PaymentMethod Method = PaymentMethod.Card;
    private const string Token = "tok_secret_card_abc123";

    [Fact]
    public void Fingerprint_SameInputs_ProducesIdentical64Hex()
    {
        var fp1 = PaymentIdempotency.Fingerprint(TripId, RiderId, DriverId, Amount, Method, Token);
        var fp2 = PaymentIdempotency.Fingerprint(TripId, RiderId, DriverId, Amount, Method, Token);

        Assert.Equal(fp1, fp2);
        Assert.Matches("^[0-9a-f]{64}$", fp1);
    }

    [Fact]
    public void Fingerprint_DoesNotExposeRawToken()
    {
        var rawToken = "super-secret-user-card-token-XYZ";
        var fp = PaymentIdempotency.Fingerprint(TripId, RiderId, DriverId, Amount, Method, rawToken);

        Assert.DoesNotContain(rawToken, fp);
        Assert.Equal(64, fp.Length);
    }

    [Fact]
    public void Fingerprint_VaryingEachField_ProducesDifferentFingerprint()
    {
        var baseline = PaymentIdempotency.Fingerprint(TripId, RiderId, DriverId, Amount, Method, Token);

        var diffTrip = PaymentIdempotency.Fingerprint(Guid.NewGuid(), RiderId, DriverId, Amount, Method, Token);
        var diffRider = PaymentIdempotency.Fingerprint(TripId, "other-rider", DriverId, Amount, Method, Token);
        var diffDriver = PaymentIdempotency.Fingerprint(TripId, RiderId, "other-driver", Amount, Method, Token);
        var diffAmount = PaymentIdempotency.Fingerprint(TripId, RiderId, DriverId, Money.Create(200_000m, "VND"), Method, Token);
        var diffMethod = PaymentIdempotency.Fingerprint(TripId, RiderId, DriverId, Amount, PaymentMethod.Wallet, Token);
        var diffToken = PaymentIdempotency.Fingerprint(TripId, RiderId, DriverId, Amount, Method, "other_token");

        Assert.NotEqual(baseline, diffTrip);
        Assert.NotEqual(baseline, diffRider);
        Assert.NotEqual(baseline, diffDriver);
        Assert.NotEqual(baseline, diffAmount);
        Assert.NotEqual(baseline, diffMethod);
        Assert.NotEqual(baseline, diffToken);
    }

    [Fact]
    public void Fingerprint_PipeDelimiterInFields_DoesNotCauseCollision()
    {
        // Without length-prefixing, "a|b" + "c" collides with "a" + "b|c"
        var fp1 = PaymentIdempotency.Fingerprint(TripId, "rider|1", "driver2", Amount, Method, Token);
        var fp2 = PaymentIdempotency.Fingerprint(TripId, "rider", "1|driver2", Amount, Method, Token);

        Assert.NotEqual(fp1, fp2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fingerprint_MissingToken_ThrowsDomainException(string? invalidToken)
    {
        Assert.Throws<DomainException>(() =>
            PaymentIdempotency.Fingerprint(TripId, RiderId, DriverId, Amount, Method, invalidToken!));
    }

    [Fact]
    public void RefundFingerprint_PipeInReason_DoesNotCauseCollision()
    {
        var paymentId = Guid.NewGuid();
        var fp1 = PaymentIdempotency.RefundFingerprint(paymentId, Amount, "reason|part1|part2");
        var fp2 = PaymentIdempotency.RefundFingerprint(paymentId, Amount, "reason|part1part2");

        Assert.NotEqual(fp1, fp2);
        Assert.Matches("^[0-9a-f]{64}$", fp1);
    }
}
