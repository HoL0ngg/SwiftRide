namespace SwiftRide.TripService.Application.Abstractions;

public interface ITripMetrics
{
    void TripCreated();
    void TripCompleted();
    void TripFailed();
}

public sealed class NullTripMetrics : ITripMetrics
{
    public static readonly NullTripMetrics Instance = new();
    public void TripCreated() { }
    public void TripCompleted() { }
    public void TripFailed() { }
}
