using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Tests.Domain;

public class FareSnapshotTests
{
    [Fact]
    public void FareSnapshot_ValidSnapshot_PreservesProperties()
    {
        var snapshot = new FareSnapshot(TripScenario.QuoteId, 74_844m, "VND");

        Assert.Equal(TripScenario.QuoteId, snapshot.QuoteId);
        Assert.Equal(74_844m, snapshot.Amount);
        Assert.Equal("VND", snapshot.Currency);
    }

    [Fact]
    public void FareSnapshot_ZeroAmount_IsValid()
    {
        var snapshot = new FareSnapshot(TripScenario.QuoteId, 0m, "VND");

        Assert.Equal(0m, snapshot.Amount);
        Assert.Equal("VND", snapshot.Currency);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "74844", "VND", "InvalidQuote")]
    [InlineData("22222222-2222-2222-2222-222222222222", "-1", "VND", "InvalidFare")]
    [InlineData("22222222-2222-2222-2222-222222222222", "1.5", "VND", "InvalidFare")]
    [InlineData("22222222-2222-2222-2222-222222222222", "74844", "USD", "InvalidFare")]
    [InlineData("22222222-2222-2222-2222-222222222222", "74844", "", "InvalidFare")]
    [InlineData("22222222-2222-2222-2222-222222222222", "74844", "   ", "InvalidFare")]
    [InlineData("22222222-2222-2222-2222-222222222222", "74844", null, "InvalidFare")]
    public void FareSnapshot_RejectsInvalidQuoteOrMoney(string quoteIdStr, string amountStr, string? currency, string expectedCode)
    {
        var quoteId = Guid.Parse(quoteIdStr);
        var amount = decimal.Parse(amountStr, System.Globalization.CultureInfo.InvariantCulture);

        var ex = Assert.Throws<TripDomainException>(() => new FareSnapshot(quoteId, amount, currency!));
        Assert.Equal(expectedCode, ex.Code);
    }
}
