using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Tests.Domain;

public class InvalidTransitionTests
{
    public static TheoryData<TripStatus> AllStates => new()
    {
        TripStatus.Requested,
        TripStatus.Quoted,
        TripStatus.DriverOffered,
        TripStatus.DriverAccepted,
        TripStatus.EnRoute,
        TripStatus.PickedUp,
        TripStatus.DroppedOff,
        TripStatus.PaymentPending,
        TripStatus.Paid,
        TripStatus.Cancelled,
        TripStatus.Failed
    };

    [Theory]
    [MemberData(nameof(AllStates))]
    public void InvalidAction_RejectsAndPreservesTripData(TripStatus initialStatus)
    {
        var trip = TripScenario.At(initialStatus);

        // Snapshot state before invalid call
        var expectedStatus = trip.Status;
        var expectedFare = trip.Fare;
        var expectedCandidate = trip.CandidateDriverId;
        var expectedOffered = trip.OfferedDriverId;
        var expectedAssigned = trip.AssignedDriverId;
        var expectedRejectionCount = trip.RejectionCount;
        var expectedPaymentKey = trip.PaymentIdempotencyKey;
        var expectedPaymentId = trip.PaymentId;
        var expectedEventCount = trip.DomainEvents.Count;
        var expectedRejectedDrivers = trip.RejectedDriverIds.ToArray();

        Action invalidAction = initialStatus switch
        {
            TripStatus.Requested => () => trip.Pickup("driver-1"),
            TripStatus.Quoted => () => trip.AcceptDriver("driver-1"),
            TripStatus.DriverOffered => () => trip.Pickup("driver-1"),
            TripStatus.DriverAccepted => () => trip.Cancel("rider-1"),
            TripStatus.EnRoute => () => trip.Dropoff("driver-1", TripScenario.DropoffUtc),
            TripStatus.PickedUp => () => trip.Cancel("rider-1"),
            TripStatus.DroppedOff => () => trip.Dropoff("driver-1", TripScenario.DropoffUtc),
            TripStatus.PaymentPending => () => trip.Cancel("rider-1"),
            TripStatus.Paid => () => trip.RequestPayment(),
            TripStatus.Cancelled => () => trip.ConfirmQuote("rider-1", TripScenario.QuoteId, "driver-1"),
            TripStatus.Failed => () => trip.ApplyQuote(new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND"), "driver-1"),
            _ => throw new ArgumentOutOfRangeException(nameof(initialStatus), initialStatus, null)
        };

        var ex = Assert.Throws<TripDomainException>(invalidAction);
        Assert.Equal("InvalidTransition", ex.Code);

        // Assert all data remains identical
        Assert.Equal(expectedStatus, trip.Status);
        Assert.Same(expectedFare, trip.Fare);
        Assert.Equal(expectedCandidate, trip.CandidateDriverId);
        Assert.Equal(expectedOffered, trip.OfferedDriverId);
        Assert.Equal(expectedAssigned, trip.AssignedDriverId);
        Assert.Equal(expectedRejectionCount, trip.RejectionCount);
        Assert.Equal(expectedPaymentKey, trip.PaymentIdempotencyKey);
        Assert.Equal(expectedPaymentId, trip.PaymentId);
        Assert.Equal(expectedEventCount, trip.DomainEvents.Count);
        Assert.Equal(expectedRejectedDrivers, trip.RejectedDriverIds.ToArray());
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void OnlyPaidCancelledAndFailed_AreTerminal(TripStatus status)
    {
        var trip = TripScenario.At(status);

        var shouldBeTerminal = status is TripStatus.Paid or TripStatus.Cancelled or TripStatus.Failed;
        Assert.Equal(shouldBeTerminal, trip.IsTerminal);
    }

    [Fact]
    public void PaymentPending_AfterRetryExhaustion_IsNotTerminal()
    {
        var trip = TripScenario.PendingPayment();
        trip.RecordPaymentFailure(PaymentFailureKind.RetryLimitReached);

        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.False(trip.IsTerminal);
    }

    [Fact]
    public void RiderCommands_RejectDifferentRiderWithoutMutation()
    {
        var requestedTrip = TripScenario.Requested();
        var exCancelReq = Assert.Throws<TripDomainException>(() => requestedTrip.Cancel("wrong-rider"));
        Assert.Equal("InvalidRider", exCancelReq.Code);
        Assert.Equal(TripStatus.Requested, requestedTrip.Status);

        var quotedTrip = TripScenario.Quoted();
        var exConfirm = Assert.Throws<TripDomainException>(() =>
            quotedTrip.ConfirmQuote("wrong-rider", TripScenario.QuoteId, "driver-1"));
        Assert.Equal("InvalidRider", exConfirm.Code);
        Assert.Equal(TripStatus.Quoted, quotedTrip.Status);

        var exReject = Assert.Throws<TripDomainException>(() => quotedTrip.RejectQuote("wrong-rider"));
        Assert.Equal("InvalidRider", exReject.Code);
        Assert.Equal(TripStatus.Quoted, quotedTrip.Status);

        var exCancelQuoted = Assert.Throws<TripDomainException>(() => quotedTrip.Cancel("wrong-rider"));
        Assert.Equal("InvalidRider", exCancelQuoted.Code);
        Assert.Equal(TripStatus.Quoted, quotedTrip.Status);

        var offeredTrip = TripScenario.Offered();
        var exCancelOffered = Assert.Throws<TripDomainException>(() => offeredTrip.Cancel("wrong-rider"));
        Assert.Equal("InvalidRider", exCancelOffered.Code);
        Assert.Equal(TripStatus.DriverOffered, offeredTrip.Status);
    }

    [Fact]
    public void DriverCommands_RejectBlankOrDifferentDriverWithoutMutation()
    {
        var offeredTrip = TripScenario.Offered();
        var exAccept = Assert.Throws<TripDomainException>(() => offeredTrip.AcceptDriver("wrong-driver"));
        Assert.Equal("InvalidDriver", exAccept.Code);
        Assert.Equal(TripStatus.DriverOffered, offeredTrip.Status);

        var exReject = Assert.Throws<TripDomainException>(() => offeredTrip.RejectDriver("wrong-driver"));
        Assert.Equal("InvalidDriver", exReject.Code);
        Assert.Equal(TripStatus.DriverOffered, offeredTrip.Status);

        var acceptedTrip = TripScenario.At(TripStatus.DriverAccepted);
        var exEnRoute = Assert.Throws<TripDomainException>(() => acceptedTrip.StartEnRoute("wrong-driver"));
        Assert.Equal("InvalidDriver", exEnRoute.Code);
        Assert.Equal(TripStatus.DriverAccepted, acceptedTrip.Status);

        var enRouteTrip = TripScenario.At(TripStatus.EnRoute);
        var exPickup = Assert.Throws<TripDomainException>(() => enRouteTrip.Pickup("wrong-driver"));
        Assert.Equal("InvalidDriver", exPickup.Code);
        Assert.Equal(TripStatus.EnRoute, enRouteTrip.Status);

        var pickedUpTrip = TripScenario.At(TripStatus.PickedUp);
        var exDropoff = Assert.Throws<TripDomainException>(() =>
            pickedUpTrip.Dropoff("wrong-driver", TripScenario.DropoffUtc));
        Assert.Equal("InvalidDriver", exDropoff.Code);
        Assert.Equal(TripStatus.PickedUp, pickedUpTrip.Status);
    }
}
