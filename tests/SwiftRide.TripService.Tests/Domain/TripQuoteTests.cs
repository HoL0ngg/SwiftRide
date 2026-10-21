using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Tests.Domain;

public class TripQuoteTests
{
    [Fact]
    public void ApplyQuote_StoresSnapshotAndCandidate()
    {
        var trip = TripScenario.Requested();
        var fare = new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND");

        trip.ApplyQuote(fare, "candidate-1");

        Assert.Equal(TripStatus.Quoted, trip.Status);
        Assert.False(trip.IsTerminal);
        Assert.False(trip.IsFareLocked);
        Assert.Same(fare, trip.Fare);
        Assert.Equal("candidate-1", trip.CandidateDriverId);
        Assert.Null(trip.AssignedDriverId);
        Assert.Null(trip.OfferedDriverId);
    }

    [Theory]
    [InlineData(null, "candidate-1", "InvalidFare")]
    [InlineData("valid-fare", null, "InvalidDriver")]
    [InlineData("valid-fare", "", "InvalidDriver")]
    [InlineData("valid-fare", "   ", "InvalidDriver")]
    public void ApplyQuote_RejectsMissingFareOrCandidate(string? fareMode, string? candidateDriverId, string expectedErrorCode)
    {
        var trip = TripScenario.Requested();
        var fare = fareMode == "valid-fare" ? new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND") : null;

        var ex = Assert.Throws<TripDomainException>(() => trip.ApplyQuote(fare!, candidateDriverId!));
        Assert.Equal(expectedErrorCode, ex.Code);
        Assert.Equal(TripStatus.Requested, trip.Status);
        Assert.Null(trip.Fare);
        Assert.Null(trip.CandidateDriverId);
    }

    [Fact]
    public void NoDriverAvailable_FailsRequestedTrip()
    {
        var trip = TripScenario.Requested();

        trip.FailNoDriverAvailable();

        Assert.Equal(TripStatus.Failed, trip.Status);
        Assert.True(trip.IsTerminal);
        Assert.Null(trip.CandidateDriverId);
        Assert.Null(trip.AssignedDriverId);
    }

    [Fact]
    public void ConfirmQuote_LocksFareAndOffersSelectedDriver()
    {
        var trip = TripScenario.Requested();
        var fare = new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND");
        trip.ApplyQuote(fare, "candidate-1");

        trip.ConfirmQuote("rider-1", TripScenario.QuoteId, "selected-driver-2");

        Assert.Equal(TripStatus.DriverOffered, trip.Status);
        Assert.False(trip.IsTerminal);
        Assert.True(trip.IsFareLocked);
        Assert.Equal("selected-driver-2", trip.OfferedDriverId);
        Assert.Equal("candidate-1", trip.CandidateDriverId);
        Assert.Same(fare, trip.Fare);
    }

    [Fact]
    public void ConfirmQuote_RejectsDifferentQuoteOrRider()
    {
        var trip = TripScenario.Requested();
        var fare = new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND");
        trip.ApplyQuote(fare, "candidate-1");

        var exRider = Assert.Throws<TripDomainException>(() =>
            trip.ConfirmQuote("different-rider", TripScenario.QuoteId, "driver-1"));
        Assert.Equal("InvalidRider", exRider.Code);
        Assert.Equal(TripStatus.Quoted, trip.Status);
        Assert.False(trip.IsFareLocked);

        var differentQuoteId = Guid.NewGuid();
        var exQuote = Assert.Throws<TripDomainException>(() =>
            trip.ConfirmQuote("rider-1", differentQuoteId, "driver-1"));
        Assert.Equal("InvalidQuote", exQuote.Code);
        Assert.Equal(TripStatus.Quoted, trip.Status);
        Assert.False(trip.IsFareLocked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfirmQuote_RejectsBlankOfferedDriver(string? blankDriver)
    {
        var trip = TripScenario.Requested();
        var fare = new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND");
        trip.ApplyQuote(fare, "candidate-1");

        var ex = Assert.Throws<TripDomainException>(() =>
            trip.ConfirmQuote("rider-1", TripScenario.QuoteId, blankDriver!));
        Assert.Equal("InvalidDriver", ex.Code);
        Assert.Equal(TripStatus.Quoted, trip.Status);
        Assert.False(trip.IsFareLocked);
        Assert.Null(trip.OfferedDriverId);
    }

    [Fact]
    public void RejectQuote_CancelsQuotedTrip()
    {
        var trip = TripScenario.Requested();
        trip.ApplyQuote(new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND"), "candidate-1");

        trip.RejectQuote("rider-1");

        Assert.Equal(TripStatus.Cancelled, trip.Status);
        Assert.True(trip.IsTerminal);
        Assert.Null(trip.OfferedDriverId);
    }

    [Fact]
    public void RejectQuote_RejectsWrongRider()
    {
        var trip = TripScenario.Requested();
        trip.ApplyQuote(new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND"), "candidate-1");

        var ex = Assert.Throws<TripDomainException>(() => trip.RejectQuote("wrong-rider"));
        Assert.Equal("InvalidRider", ex.Code);
        Assert.Equal(TripStatus.Quoted, trip.Status);
    }

    [Theory]
    [InlineData(TripStatus.Requested)]
    [InlineData(TripStatus.Quoted)]
    [InlineData(TripStatus.DriverOffered)]
    public void Cancel_IsAllowedOnlyInPreAcceptanceStates(TripStatus targetInitialState)
    {
        var trip = TripScenario.Requested();
        if (targetInitialState == TripStatus.Quoted)
        {
            trip.ApplyQuote(new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND"), "candidate-1");
        }
        else if (targetInitialState == TripStatus.DriverOffered)
        {
            trip.ApplyQuote(new FareSnapshot(TripScenario.QuoteId, TripScenario.Fare, "VND"), "candidate-1");
            trip.ConfirmQuote("rider-1", TripScenario.QuoteId, "driver-1");
        }

        trip.Cancel("rider-1");

        Assert.Equal(TripStatus.Cancelled, trip.Status);
        Assert.True(trip.IsTerminal);
        Assert.Null(trip.OfferedDriverId);
    }

    [Fact]
    public void Cancel_RejectsWrongRider()
    {
        var trip = TripScenario.Requested();

        var ex = Assert.Throws<TripDomainException>(() => trip.Cancel("wrong-rider"));
        Assert.Equal("InvalidRider", ex.Code);
        Assert.Equal(TripStatus.Requested, trip.Status);
    }
}
