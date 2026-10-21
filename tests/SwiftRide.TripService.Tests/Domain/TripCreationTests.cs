using SwiftRide.TripService.Domain;

namespace SwiftRide.TripService.Tests.Domain;

public class TripCreationTests
{
    [Fact]
    public void Request_StartsRequestedWithoutFareOrDriver()
    {
        var trip = Trip.Request(TripScenario.TripId, "rider-1");

        Assert.Equal(TripStatus.Requested, trip.Status);
        Assert.False(trip.IsTerminal);
        Assert.Null(trip.Fare);
        Assert.Null(trip.AssignedDriverId);
        Assert.Equal(0, trip.RejectionCount);
        Assert.Equal(3, trip.MaxDriverRejections);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "rider-1", 3, "InvalidTripId")]
    [InlineData("11111111-1111-1111-1111-111111111111", null, 3, "InvalidRider")]
    [InlineData("11111111-1111-1111-1111-111111111111", "", 3, "InvalidRider")]
    [InlineData("11111111-1111-1111-1111-111111111111", "   ", 3, "InvalidRider")]
    [InlineData("11111111-1111-1111-1111-111111111111", "rider-1", -1, "InvalidRejectionLimit")]
    public void Request_RejectsInvalidIdentityOrLimit(string tripIdStr, string? riderId, int maxRejections, string expectedErrorCode)
    {
        var tripId = Guid.Parse(tripIdStr);

        var ex = Assert.Throws<TripDomainException>(() => Trip.Request(tripId, riderId!, maxRejections));
        Assert.Equal(expectedErrorCode, ex.Code);
    }

    [Fact]
    public void Request_AllowsZeroMaxRejections()
    {
        var trip = Trip.Request(TripScenario.TripId, "rider-1", maxDriverRejections: 0);

        Assert.Equal(TripStatus.Requested, trip.Status);
        Assert.Equal(0, trip.MaxDriverRejections);
    }
}
