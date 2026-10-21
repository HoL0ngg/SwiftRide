using SwiftRide.TripService.Domain;

namespace SwiftRide.TripService.Tests.Domain;

public class DriverOfferTests
{
    [Fact]
    public void FourthRejection_ExceedsDefaultLimit()
    {
        var trip = TripScenario.Offered();
        for (var i = 1; i <= 3; i++)
        {
            trip.RejectDriver($"driver-{i}");
            Assert.Equal(TripStatus.DriverOffered, trip.Status);
            Assert.Equal(i, trip.RejectionCount);
            Assert.True(trip.IsRematchPending);
            Assert.Null(trip.OfferedDriverId);
            trip.OfferReplacementDriver($"driver-{i + 1}");
            Assert.False(trip.IsRematchPending);
            Assert.Equal($"driver-{i + 1}", trip.OfferedDriverId);
        }

        trip.RejectDriver("driver-4");
        Assert.Equal(TripStatus.Failed, trip.Status);
        Assert.Equal(4, trip.RejectionCount);
        Assert.True(trip.IsTerminal);
    }

    [Fact]
    public void ZeroRejectionsLimit_FailsOnFirstRejection()
    {
        var trip = TripScenario.Offered(maxDriverRejections: 0);

        trip.RejectDriver("driver-1");

        Assert.Equal(TripStatus.Failed, trip.Status);
        Assert.Equal(1, trip.RejectionCount);
        Assert.True(trip.IsTerminal);
    }

    [Fact]
    public void OneRejectionLimit_AllowsOneRematchThenFailsOnSecond()
    {
        var trip = TripScenario.Offered(maxDriverRejections: 1);

        trip.RejectDriver("driver-1");
        Assert.Equal(TripStatus.DriverOffered, trip.Status);
        Assert.Equal(1, trip.RejectionCount);
        Assert.True(trip.IsRematchPending);

        trip.OfferReplacementDriver("driver-2");
        Assert.Equal("driver-2", trip.OfferedDriverId);

        trip.RejectDriver("driver-2");
        Assert.Equal(TripStatus.Failed, trip.Status);
        Assert.Equal(2, trip.RejectionCount);
        Assert.True(trip.IsTerminal);
    }

    [Fact]
    public void Rematch_PreservesFareAndRejectsPreviousDrivers()
    {
        var trip = TripScenario.Offered();
        var originalFare = trip.Fare;

        trip.RejectDriver("driver-1");
        Assert.Contains("driver-1", trip.RejectedDriverIds);

        trip.OfferReplacementDriver("driver-2");

        Assert.Equal(TripStatus.DriverOffered, trip.Status);
        Assert.Same(originalFare, trip.Fare);
        Assert.Equal(1, trip.RejectionCount);
        Assert.Equal("driver-2", trip.OfferedDriverId);

        // Cannot re-offer a previously rejected driver
        trip.RejectDriver("driver-2");
        var ex = Assert.Throws<TripDomainException>(() => trip.OfferReplacementDriver("driver-1"));
        Assert.Equal("DriverAlreadyRejected", ex.Code);
    }

    [Fact]
    public void RejectDriver_RequiresCurrentOffer()
    {
        var trip = TripScenario.Offered();

        var ex = Assert.Throws<TripDomainException>(() => trip.RejectDriver("wrong-driver"));
        Assert.Equal("InvalidDriver", ex.Code);
        Assert.Equal(0, trip.RejectionCount);
        Assert.Equal("driver-1", trip.OfferedDriverId);
        Assert.Equal(TripStatus.DriverOffered, trip.Status);
    }

    [Fact]
    public void NoCandidateAfterRejection_FailsTrip()
    {
        var trip = TripScenario.Offered();
        trip.RejectDriver("driver-1");
        Assert.True(trip.IsRematchPending);

        trip.FailNoDriverAvailable();

        Assert.Equal(TripStatus.Failed, trip.Status);
        Assert.True(trip.IsTerminal);
    }

    [Fact]
    public void ReplacementOffer_CannotOverwriteCurrentInvitation()
    {
        var trip = TripScenario.Offered();

        var ex = Assert.Throws<TripDomainException>(() => trip.OfferReplacementDriver("driver-2"));
        Assert.Equal("InvalidTransition", ex.Code);
        Assert.Equal("driver-1", trip.OfferedDriverId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReplacementOffer_RejectsBlankDriverWithoutMutation(string? blankDriver)
    {
        var trip = TripScenario.Offered();
        trip.RejectDriver("driver-1");

        var ex = Assert.Throws<TripDomainException>(() => trip.OfferReplacementDriver(blankDriver!));
        Assert.Equal("InvalidDriver", ex.Code);
        Assert.True(trip.IsRematchPending);
        Assert.Equal(1, trip.RejectionCount);
    }
}
