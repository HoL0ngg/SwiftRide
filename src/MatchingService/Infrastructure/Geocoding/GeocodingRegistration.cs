using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SwiftRide.MatchingService.Application.Geocoding;

namespace SwiftRide.MatchingService.Infrastructure.Geocoding;

public static class GeocodingRegistration
{
    public static IServiceCollection AddGeocoding(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NominatimOptions>().Bind(configuration.GetSection("Geocoding"))
            .Validate(options => Uri.TryCreate(options.SearchUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo),
                "Geocoding:SearchUrl must be an HTTPS URL without credentials, query or fragment.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.UserAgent), "Geocoding:UserAgent is required.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient("Nominatim", (provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<NominatimOptions>>().Value;
                client.BaseAddress = new Uri(options.SearchUrl);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
                client.Timeout = TimeSpan.FromSeconds(3);
                client.MaxResponseContentBufferSize = 65_536;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        // shortcut: pacing and cache are process-local; use a shared provider before scaling replicas.
        services.AddSingleton<IAddressSearch, NominatimAddressSearch>();
        return services;
    }
}
