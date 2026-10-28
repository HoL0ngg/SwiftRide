namespace SwiftRide.MatchingService.Application.Geocoding;

public record AddressResult(string Id, string DisplayName, double Latitude, double Longitude);

public interface IAddressSearch
{
    Task<IReadOnlyList<AddressResult>> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed class GeocodingUnavailableException() : Exception("Address search is temporarily unavailable.");
