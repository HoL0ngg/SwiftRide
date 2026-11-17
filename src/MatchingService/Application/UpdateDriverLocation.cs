using SwiftRide.MatchingService.Domain;

namespace SwiftRide.MatchingService.Application;

public sealed class UpdateDriverLocation(IDriverLocationWriter writer, TimeProvider clock)
{
    public Task ExecuteAsync(string driverId, GeoPoint position, bool isOnline, CancellationToken cancellationToken, string vehicleType = "motorbike")
    {
        if (string.IsNullOrWhiteSpace(driverId) || driverId.Length > 128)
            throw new MatchingDomainException("Driver ID must have between 1 and 128 characters.");
        vehicleType = VehicleTypes.Normalize(vehicleType);
        return writer.UpsertAsync(new(driverId, position, isOnline, clock.GetUtcNow(), vehicleType), cancellationToken);
    }
}
