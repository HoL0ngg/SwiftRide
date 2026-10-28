namespace SwiftRide.MatchingService.Infrastructure.Geocoding;

public sealed class NominatimOptions
{
    public string SearchUrl { get; set; } = "https://nominatim.openstreetmap.org/search";
    public string UserAgent { get; set; } = "SwiftRideCourseDemo/1.0";
}
