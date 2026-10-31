namespace SwiftRide.TripService.Application.Trips;

public sealed record TripActor(string Id, string Role)
{
    public bool IsRider => string.Equals(Role, "rider", StringComparison.OrdinalIgnoreCase);
    public bool IsDriver => string.Equals(Role, "driver", StringComparison.OrdinalIgnoreCase);
}
