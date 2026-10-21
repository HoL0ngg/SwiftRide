using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

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

    public static Trip Quoted(int maxDriverRejections = 3)
    {
        var trip = Requested(maxDriverRejections);
        trip.ApplyQuote(new FareSnapshot(QuoteId, Fare, "VND"), DriverId);
        return trip;
    }

    public static Trip Offered(int maxDriverRejections = 3)
    {
        var trip = Quoted(maxDriverRejections);
        trip.ConfirmQuote(RiderId, QuoteId, DriverId);
        return trip;
    }

    public static Trip PickedUp(int maxDriverRejections = 3)
    {
        var trip = Offered(maxDriverRejections);
        trip.AcceptDriver(DriverId);
        trip.StartEnRoute(DriverId);
        trip.Pickup(DriverId);
        return trip;
    }

    public static Trip PendingPayment(int maxDriverRejections = 3)
    {
        var trip = PickedUp(maxDriverRejections);
        trip.Dropoff(DriverId, DropoffUtc);
        trip.RequestPayment();
        return trip;
    }

    public static Trip At(TripStatus status)
    {
        return status switch
        {
            TripStatus.Requested => Requested(),
            TripStatus.Quoted => Quoted(),
            TripStatus.DriverOffered => Offered(),
            TripStatus.DriverAccepted => CreateDriverAccepted(),
            TripStatus.EnRoute => CreateEnRoute(),
            TripStatus.PickedUp => PickedUp(),
            TripStatus.DroppedOff => CreateDroppedOff(),
            TripStatus.PaymentPending => PendingPayment(),
            TripStatus.Paid => CreatePaid(),
            TripStatus.Cancelled => CreateCancelled(),
            TripStatus.Failed => CreateFailed(),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
    }

    private static Trip CreateDriverAccepted()
    {
        var trip = Offered();
        trip.AcceptDriver(DriverId);
        return trip;
    }

    private static Trip CreateEnRoute()
    {
        var trip = CreateDriverAccepted();
        trip.StartEnRoute(DriverId);
        return trip;
    }

    private static Trip CreateDroppedOff()
    {
        var trip = PickedUp();
        trip.Dropoff(DriverId, DropoffUtc);
        return trip;
    }

    private static Trip CreatePaid()
    {
        var trip = PendingPayment();
        trip.SettlePayment(new PaymentSettlement(PaymentId, TripId, Fare, "VND", trip.PaymentIdempotencyKey!));
        return trip;
    }

    private static Trip CreateCancelled()
    {
        var trip = Requested();
        trip.Cancel(RiderId);
        return trip;
    }

    private static Trip CreateFailed()
    {
        var trip = Requested();
        trip.FailNoDriverAvailable();
        return trip;
    }
}
