using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.Events;

namespace SwiftRide.TripService.Tests.Domain;

public class TripProgressTests
{
    [Fact]
    public void AcceptDriver_AssignsOnlyCurrentlyOfferedDriver()
    {
        var trip = TripScenario.Offered();

        trip.AcceptDriver("driver-1");

        Assert.Equal(TripStatus.DriverAccepted, trip.Status);
        Assert.Equal("driver-1", trip.AssignedDriverId);
        Assert.Null(trip.OfferedDriverId);
    }

    [Fact]
    public void AcceptDriver_RejectsWrongDriverOrPendingRematch()
    {
        var trip = TripScenario.Offered();

        var exWrong = Assert.Throws<TripDomainException>(() => trip.AcceptDriver("wrong-driver"));
        Assert.Equal("InvalidDriver", exWrong.Code);
        Assert.Equal(TripStatus.DriverOffered, trip.Status);
        Assert.Null(trip.AssignedDriverId);
        Assert.Equal("driver-1", trip.OfferedDriverId);

        // Put into pending rematch
        trip.RejectDriver("driver-1");
        var exRematch = Assert.Throws<TripDomainException>(() => trip.AcceptDriver("driver-1"));
        Assert.Equal("RematchPending", exRematch.Code);
        Assert.Equal(TripStatus.DriverOffered, trip.Status);
        Assert.Null(trip.AssignedDriverId);
    }

    [Fact]
    public void AssignedDriver_MustAdvanceInOrder()
    {
        var trip = TripScenario.Offered();

        trip.AcceptDriver("driver-1");
        Assert.Equal(TripStatus.DriverAccepted, trip.Status);

        trip.StartEnRoute("driver-1");
        Assert.Equal(TripStatus.EnRoute, trip.Status);

        trip.Pickup("driver-1");
        Assert.Equal(TripStatus.PickedUp, trip.Status);

        trip.Dropoff("driver-1", TripScenario.DropoffUtc);
        Assert.Equal(TripStatus.DroppedOff, trip.Status);
    }

    [Theory]
    [InlineData("StartEnRoute")]
    [InlineData("Pickup")]
    [InlineData("Dropoff")]
    public void Progress_RejectsAnotherDriver(string step)
    {
        var trip = TripScenario.Offered();
        trip.AcceptDriver("driver-1");

        if (step == "StartEnRoute")
        {
            var ex = Assert.Throws<TripDomainException>(() => trip.StartEnRoute("wrong-driver"));
            Assert.Equal("InvalidDriver", ex.Code);
            Assert.Equal(TripStatus.DriverAccepted, trip.Status);
        }
        else if (step == "Pickup")
        {
            trip.StartEnRoute("driver-1");
            var ex = Assert.Throws<TripDomainException>(() => trip.Pickup("wrong-driver"));
            Assert.Equal("InvalidDriver", ex.Code);
            Assert.Equal(TripStatus.EnRoute, trip.Status);
        }
        else if (step == "Dropoff")
        {
            trip.StartEnRoute("driver-1");
            trip.Pickup("driver-1");
            var ex = Assert.Throws<TripDomainException>(() => trip.Dropoff("wrong-driver", TripScenario.DropoffUtc));
            Assert.Equal("InvalidDriver", ex.Code);
            Assert.Equal(TripStatus.PickedUp, trip.Status);
            Assert.Empty(trip.DomainEvents);
        }
    }

    [Theory]
    [InlineData(TripStatus.DriverAccepted)]
    [InlineData(TripStatus.EnRoute)]
    [InlineData(TripStatus.PickedUp)]
    [InlineData(TripStatus.DroppedOff)]
    public void Cancel_AfterDriverAcceptance_IsInvalid(TripStatus status)
    {
        var trip = TripScenario.Offered();
        trip.AcceptDriver("driver-1");

        if (status == TripStatus.EnRoute)
        {
            trip.StartEnRoute("driver-1");
        }
        else if (status == TripStatus.PickedUp)
        {
            trip.StartEnRoute("driver-1");
            trip.Pickup("driver-1");
        }
        else if (status == TripStatus.DroppedOff)
        {
            trip.StartEnRoute("driver-1");
            trip.Pickup("driver-1");
            trip.Dropoff("driver-1", TripScenario.DropoffUtc);
        }

        var ex = Assert.Throws<TripDomainException>(() => trip.Cancel("rider-1"));
        Assert.Equal("InvalidTransition", ex.Code);
        Assert.Equal(status, trip.Status);
    }

    [Fact]
    public void Dropoff_EmitsRideCompletedWithAgreedFare()
    {
        var trip = TripScenario.PickedUp();

        trip.Dropoff("driver-1", TripScenario.DropoffUtc);

        Assert.Equal(TripStatus.DroppedOff, trip.Status);
        var completed = Assert.Single(trip.DomainEvents.OfType<RideCompleted>());
        Assert.Equal(TripScenario.TripId, completed.TripId);
        Assert.Equal(TripScenario.RiderId, completed.RiderId);
        Assert.Equal("driver-1", completed.DriverId);
        Assert.Equal(TripScenario.QuoteId, completed.QuoteId);
        Assert.Equal(74_844m, completed.FareAmount);
        Assert.Equal("VND", completed.Currency);
        Assert.Equal(TripScenario.DropoffUtc, completed.OccurredAtUtc);
    }

    [Fact]
    public void Dropoff_RepeatedCommand_DoesNotAppendAnotherEvent()
    {
        var trip = TripScenario.PickedUp();
        trip.Dropoff("driver-1", TripScenario.DropoffUtc);

        var ex = Assert.Throws<TripDomainException>(() => trip.Dropoff("driver-1", TripScenario.DropoffUtc));
        Assert.Equal("InvalidTransition", ex.Code);
        Assert.Equal(TripStatus.DroppedOff, trip.Status);
        Assert.Single(trip.DomainEvents);
    }

    [Fact]
    public void Dropoff_NormalizesEventTimeToUtc()
    {
        var trip = TripScenario.PickedUp();
        var localTime = DateTimeOffset.Parse("2026-10-19T07:00:00+07:00");

        trip.Dropoff("driver-1", localTime);

        var completed = Assert.Single(trip.DomainEvents.OfType<RideCompleted>());
        Assert.Equal(TimeSpan.Zero, completed.OccurredAtUtc.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T00:00:00+00:00"), completed.OccurredAtUtc);
    }
}
