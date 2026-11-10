namespace SwiftRide.TripService.Application.Trips;

public sealed class TripLifecycleOptions
{
    public int MaxDriverRejections { get; set; } = 3;
    public string[] SupportedPricingModes { get; set; } = ["standard"];
    public string PaymentTransport { get; set; } = "Rest";

    /// <summary>
    /// How Trip talks to Matching: "Rest" (synchronous /match and /quote) or "Events" (RabbitMQ request/reply events,
    /// the trip stays <c>Requested</c> / rematch-pending until Matching answers). "Events" needs messaging enabled.
    /// </summary>
    public string MatchingTransport { get; set; } = "Rest";

    public bool UsesEventMatching => string.Equals(MatchingTransport, "Events", StringComparison.OrdinalIgnoreCase);
}
