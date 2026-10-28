using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftRide.MatchingService.Application.Geocoding;

namespace SwiftRide.MatchingService.Infrastructure.Geocoding;

public sealed class NominatimAddressSearch(IHttpClientFactory clients, TimeProvider clock) : IAddressSearch, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private long? previousRequest;
    private DateTimeOffset cooldownUntil;

    public async Task<IReadOnlyList<AddressResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length is < 3 or > 200)
            throw new ArgumentException("Address query must contain between 3 and 200 characters.", nameof(query));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        var entered = false;
        try
        {
            await gate.WaitAsync(budget.Token);
            entered = true;
            if (cache.TryGetValue(query, out var cached) && cached.ExpiresAt > clock.GetUtcNow())
                return cached.Results;
            if (cooldownUntil > clock.GetUtcNow())
                throw new GeocodingUnavailableException();

            using var client = clients.CreateClient("Nominatim");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt - 1) + Random.Shared.Next(251)), budget.Token);
                await WaitForRequestSlotAsync(budget.Token);
                try
                {
                    using var response = await client.GetAsync(
                        $"?format=jsonv2&countrycodes=vn&limit=5&q={Uri.EscapeDataString(query)}", budget.Token);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var now = clock.GetUtcNow();
                        cooldownUntil = response.Headers.RetryAfter?.Date
                            ?? now.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                        if (cooldownUntil < now.AddSeconds(1))
                            cooldownUntil = now.AddSeconds(1);
                        throw new GeocodingUnavailableException();
                    }
                    if (IsTransient(response.StatusCode) && attempt < 2)
                        continue;
                    if (!response.IsSuccessStatusCode)
                        throw new GeocodingUnavailableException();

                    var body = await response.Content.ReadAsStringAsync(budget.Token);
                    var results = Parse(body);
                    if (cache.Count >= 256)
                        cache.Remove(cache.MinBy(entry => entry.Value.ExpiresAt).Key);
                    cache[query] = new CacheEntry(results, clock.GetUtcNow().AddHours(24));
                    return results;
                }
                catch (HttpRequestException exception) when (attempt < 2 && exception.HttpRequestError is
                    HttpRequestError.Unknown or HttpRequestError.NameResolutionError
                    or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded) { }
                catch (OperationCanceledException) when (!budget.IsCancellationRequested && attempt < 2) { }
                finally
                {
                    previousRequest = Stopwatch.GetTimestamp();
                }
            }
            throw new GeocodingUnavailableException();
        }
        catch (HttpRequestException)
        {
            throw new GeocodingUnavailableException();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeocodingUnavailableException();
        }
        finally
        {
            if (entered)
                gate.Release();
        }
    }

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        if (previousRequest is { } previous)
        {
            // Recheck elapsed time because timers may wake before their requested deadline.
            while (true)
            {
                var remaining = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(previous);
                if (remaining <= TimeSpan.Zero)
                    break;
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(1), cancellationToken);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        previousRequest = Stopwatch.GetTimestamp();
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static IReadOnlyList<AddressResult> Parse(string body)
    {
        try
        {
            var places = JsonSerializer.Deserialize<NominatimPlace[]>(body);
            if (places is null || places.Length > 5)
                throw new GeocodingUnavailableException();
            var results = new List<AddressResult>(places.Length);
            foreach (var place in places)
            {
                if (place is null || place.PlaceId <= 0 || string.IsNullOrWhiteSpace(place.DisplayName)
                    || !double.TryParse(place.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                    || !double.TryParse(place.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
                    || !double.IsFinite(latitude) || latitude is < -90 or > 90
                    || !double.IsFinite(longitude) || longitude is < -180 or > 180)
                    throw new GeocodingUnavailableException();
                results.Add(new AddressResult(place.PlaceId.ToString(CultureInfo.InvariantCulture), place.DisplayName, latitude, longitude));
            }
            return results.AsReadOnly();
        }
        catch (JsonException)
        {
            throw new GeocodingUnavailableException();
        }
    }

    public void Dispose() => gate.Dispose();

    private sealed record CacheEntry(IReadOnlyList<AddressResult> Results, DateTimeOffset ExpiresAt);
    private sealed record NominatimPlace(
        [property: JsonPropertyName("place_id")] long PlaceId,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("lat")] string? Lat,
        [property: JsonPropertyName("lon")] string? Lon);
}
