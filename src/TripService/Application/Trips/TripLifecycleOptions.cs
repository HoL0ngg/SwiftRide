namespace SwiftRide.TripService.Application.Trips;

public sealed class TripLifecycleOptions
{
    public int MaxDriverRejections { get; set; } = 3;
    public string[] SupportedPricingModes { get; set; } = ["standard"];
    public string PaymentTransport { get; set; } = "Rest";
}
