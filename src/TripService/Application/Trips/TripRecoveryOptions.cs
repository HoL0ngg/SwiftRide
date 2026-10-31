namespace SwiftRide.TripService.Application.Trips;

public sealed class TripRecoveryOptions
{
    public int MatchingMaxAttempts { get; set; } = 5;
    public TimeSpan MatchingLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MatchingTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
