using SwiftRide.TripService.Domain;

namespace SwiftRide.TripService.Tests.Domain;

public static class TripScenario
{
    public static readonly Guid TripId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid QuoteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid PaymentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public const string RiderId = "rider-1";
    public const string DriverId = "driver-1";
    public const decimal Fare = 74844m;
    public static readonly DateTimeOffset DropoffUtc = DateTimeOffset.Parse("2026-10-19T00:00:00+00:00");
    public static readonly string PaymentKey = "trip-11111111-1111-1111-1111-111111111111-payment";

    public static Trip Requested(int maxDriverRejections = 3) =>
        Trip.Request(TripId, RiderId, maxDriverRejections);
}
