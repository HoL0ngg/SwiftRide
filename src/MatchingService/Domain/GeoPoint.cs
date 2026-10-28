namespace SwiftRide.MatchingService.Domain;

public sealed record GeoPoint
{
    public double Latitude { get; }
    public double Longitude { get; }

    public GeoPoint(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new MatchingDomainException("Coordinates are outside their valid range.");
        Latitude = latitude;
        Longitude = longitude;
    }

    public double DistanceKmTo(GeoPoint other)
    {
        const double radians = Math.PI / 180;
        var dLat = (other.Latitude - Latitude) * radians;
        var dLon = (other.Longitude - Longitude) * radians;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Latitude * radians)
            * Math.Cos(other.Latitude * radians) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 6371 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
