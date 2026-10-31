using SwiftRide.TripService.Application.Abstractions;

namespace SwiftRide.TripService.Api.Observability;

public sealed class TripMetrics : ITripMetrics
{
    public void TripCreated() => TripTelemetry.TripsCreated.Add(1);
    public void TripCompleted() => TripTelemetry.TripsCompleted.Add(1);
    public void TripFailed() => TripTelemetry.TripsFailed.Add(1);
}
