using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Tests.Domain;

public class TripRestorationTests
{
    [Fact]
    public void Restore_DoesNotReplayEvents()
    {
        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            "rider-1",
            TripStatus.DroppedOff,
            new FareSnapshot(Guid.NewGuid(), 50000m, "VND"),
            true,
            "cand-1",
            null,
            "driver-1",
            0,
            3,
            Array.Empty<string>(),
            null,
            null,
            null);

        var restored = Trip.Restore(snapshot);

        Assert.Empty(restored.DomainEvents);
        Assert.Equal(TripStatus.DroppedOff, restored.Status);
    }

    [Theory]
    [InlineData(TripStatus.Requested)]
    [InlineData(TripStatus.Quoted)]
    [InlineData(TripStatus.DriverOffered)]
    [InlineData(TripStatus.DriverAccepted)]
    [InlineData(TripStatus.EnRoute)]
    [InlineData(TripStatus.PickedUp)]
    [InlineData(TripStatus.DroppedOff)]
    [InlineData(TripStatus.PaymentPending)]
    [InlineData(TripStatus.Paid)]
    [InlineData(TripStatus.Cancelled)]
    [InlineData(TripStatus.Failed)]
    public void RoundTrip_All11Statuses_PreservesState(TripStatus status)
    {
        var fare = status == TripStatus.Requested ? null : new FareSnapshot(Guid.NewGuid(), 60000m, "VND");
        var isLocked = status != TripStatus.Requested && status != TripStatus.Quoted;
        var driverId = status is TripStatus.Requested or TripStatus.Quoted ? null : "driver-assigned";
        var candidateId = status == TripStatus.Requested ? null : "candidate-1";
        var offeredId = status == TripStatus.DriverOffered ? "driver-offered" : null;
        var paymentId = status == TripStatus.Paid ? Guid.NewGuid() : (Guid?)null;

        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            "rider-roundtrip",
            status,
            fare,
            isLocked,
            candidateId,
            offeredId,
            driverId,
            1,
            3,
            new[] { "driver-rejected-1" },
            "key-123",
            paymentId,
            PaymentFailureKind.Transient);

        var restored = Trip.Restore(snapshot);
        var roundTrip = restored.ToSnapshot();

        Assert.Equal(snapshot.Id, roundTrip.Id);
        Assert.Equal(snapshot.RiderId, roundTrip.RiderId);
        Assert.Equal(snapshot.Status, roundTrip.Status);
        Assert.Equal(snapshot.IsFareLocked, roundTrip.IsFareLocked);
        Assert.Equal(snapshot.CandidateDriverId, roundTrip.CandidateDriverId);
        Assert.Equal(snapshot.OfferedDriverId, roundTrip.OfferedDriverId);
        Assert.Equal(snapshot.AssignedDriverId, roundTrip.AssignedDriverId);
        Assert.Equal(snapshot.RejectionCount, roundTrip.RejectionCount);
        Assert.Equal(snapshot.RejectedDriverIds, roundTrip.RejectedDriverIds);
        Assert.Equal(snapshot.PaymentIdempotencyKey, roundTrip.PaymentIdempotencyKey);
        Assert.Equal(snapshot.PaymentId, roundTrip.PaymentId);
        Assert.Equal(snapshot.LastPaymentFailure, roundTrip.LastPaymentFailure);
    }

    [Fact]
    public void RoundTrip_RematchPending_PreservesNullOfferedDriver()
    {
        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            "rider-rematch",
            TripStatus.DriverOffered,
            new FareSnapshot(Guid.NewGuid(), 40000m, "VND"),
            true,
            "cand-1",
            null, // OfferedDriverId is null -> RematchPending
            null,
            1,
            3,
            new[] { "driver-old" },
            null,
            null,
            null);

        var restored = Trip.Restore(snapshot);

        Assert.True(restored.IsRematchPending);
        Assert.Null(restored.OfferedDriverId);
    }

    [Fact]
    public void Restore_PaidMissingPaymentId_Throws()
    {
        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            "rider-1",
            TripStatus.Paid,
            new FareSnapshot(Guid.NewGuid(), 50000m, "VND"),
            true,
            "cand-1",
            null,
            "driver-1",
            0,
            3,
            Array.Empty<string>(),
            "key",
            null, // Missing paymentId for Paid trip
            null);

        Assert.Throws<TripDomainException>(() => Trip.Restore(snapshot));
    }

    [Fact]
    public void Restore_LockedFareMissingQuote_Throws()
    {
        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            "rider-1",
            TripStatus.DriverOffered,
            null, // Missing Fare but IsFareLocked is true
            true,
            "cand-1",
            "driver-1",
            null,
            0,
            3,
            Array.Empty<string>(),
            null,
            null,
            null);

        Assert.Throws<TripDomainException>(() => Trip.Restore(snapshot));
    }

    [Fact]
    public void Restore_DriverAcceptedMissingAssignedDriver_Throws()
    {
        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            "rider-1",
            TripStatus.DriverAccepted,
            new FareSnapshot(Guid.NewGuid(), 50000m, "VND"),
            true,
            "cand-1",
            null,
            null, // Missing AssignedDriverId
            0,
            3,
            Array.Empty<string>(),
            null,
            null,
            null);

        Assert.Throws<TripDomainException>(() => Trip.Restore(snapshot));
    }

    [Fact]
    public void Restore_RejectionCountMismatchWithSet_Throws()
    {
        var snapshot = new TripSnapshot(
            Guid.NewGuid(),
            "rider-1",
            TripStatus.DriverOffered,
            new FareSnapshot(Guid.NewGuid(), 50000m, "VND"),
            true,
            "cand-1",
            "driver-1",
            null,
            2, // Count is 2
            3,
            new[] { "driver-only-one" }, // Set has 1 item
            null,
            null,
            null);

        Assert.Throws<TripDomainException>(() => Trip.Restore(snapshot));
    }

    [Fact]
    public void FareSnapshot_MaxBoundary_999999999999999999_Valid()
    {
        var quoteId = Guid.NewGuid();
        var fare = new FareSnapshot(quoteId, 999_999_999_999_999_999m, "VND");
        Assert.Equal(999_999_999_999_999_999m, fare.Amount);
    }

    [Fact]
    public void FareSnapshot_MaxBoundary_1000000000000000000_Throws()
    {
        var quoteId = Guid.NewGuid();
        Assert.Throws<TripDomainException>(() => new FareSnapshot(quoteId, 1_000_000_000_000_000_000m, "VND"));
    }

    [Fact]
    public void ConfirmQuote_TwoParameters_LocksFareAndEntersRematchPending()
    {
        var trip = Trip.Request(Guid.NewGuid(), "rider-confirm");
        var quoteId = Guid.NewGuid();
        trip.ApplyQuote(new FareSnapshot(quoteId, 50000m, "VND"), "cand-1");

        trip.ConfirmQuote("rider-confirm", quoteId);

        Assert.Equal(TripStatus.DriverOffered, trip.Status);
        Assert.True(trip.IsFareLocked);
        Assert.Null(trip.OfferedDriverId);
        Assert.True(trip.IsRematchPending);
    }

    [Fact]
    public void ConfirmQuote_TwoParameters_WrongRiderOrQuote_DoesNotMutate()
    {
        var trip = Trip.Request(Guid.NewGuid(), "rider-confirm");
        var quoteId = Guid.NewGuid();
        trip.ApplyQuote(new FareSnapshot(quoteId, 50000m, "VND"), "cand-1");

        Assert.Throws<TripDomainException>(() => trip.ConfirmQuote("wrong-rider", quoteId));
        Assert.Equal(TripStatus.Quoted, trip.Status);
        Assert.False(trip.IsFareLocked);

        Assert.Throws<TripDomainException>(() => trip.ConfirmQuote("rider-confirm", Guid.NewGuid()));
        Assert.Equal(TripStatus.Quoted, trip.Status);
        Assert.False(trip.IsFareLocked);
    }
}
