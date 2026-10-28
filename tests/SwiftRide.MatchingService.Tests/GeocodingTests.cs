using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.MatchingService.Application.Geocoding;
using SwiftRide.MatchingService.Infrastructure.Geocoding;

namespace SwiftRide.MatchingService.Tests;

public sealed class GeocodingTests
{
    private const string AddressJson = """
        [{"place_id":123,"display_name":"Ben Thanh, Ho Chi Minh City, Vietnam","lat":"10.7721","lon":"106.6983"}]
        """;

    [Fact]
    public async Task Search_encodes_query_and_maps_only_address_fields()
    {
        using var handler = new RecordingHandler(_ => Json(AddressJson));
        using var services = CreateServices(handler);
        var results = await Search(services).SearchAsync("  Hà Nội & limit=99  ", default);

        Assert.Equal(new AddressResult("123", "Ben Thanh, Ho Chi Minh City, Vietnam", 10.7721, 106.6983), Assert.Single(results));
        Assert.Equal("https://nominatim.openstreetmap.org/search?format=jsonv2&countrycodes=vn&limit=5&q=H%C3%A0%20N%E1%BB%99i%20%26%20limit%3D99", Assert.Single(handler.Urls));
        Assert.Equal("SwiftRideCourseDemo/1.0", Assert.Single(handler.UserAgents));
    }

    [Theory]
    [InlineData(AddressJson)]
    [InlineData("[]")]
    public async Task Cache_reuses_positive_and_empty_results(string json)
    {
        using var handler = new RecordingHandler(_ => Json(json));
        using var services = CreateServices(handler);
        var search = Search(services);

        var first = await search.SearchAsync("Ha Noi", default);
        var second = await search.SearchAsync(" Ha Noi ", default);

        Assert.Equal(first, second);
        Assert.Single(handler.Urls);
        Assert.Same(search, Search(services));
    }

    [Fact]
    public async Task Cache_expires_after_one_day()
    {
        var clock = new TestClock();
        using var handler = new RecordingHandler(_ => Json(AddressJson));
        using var services = CreateServices(handler, clock);
        var search = Search(services);
        await search.SearchAsync("Ha Noi", default);
        clock.Now = clock.Now.AddHours(25);
        await search.SearchAsync("Ha Noi", default);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(201)]
    public async Task Invalid_query_is_rejected_without_network(int length)
    {
        using var handler = new RecordingHandler(_ => Json(AddressJson));
        using var services = CreateServices(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Search(services).SearchAsync(new string('a', length), default));
        Assert.Empty(handler.Urls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"error\":\"upstream failure\"}")]
    [InlineData("[{\"place_id\":123,\"display_name\":\"Place\",\"lat\":\"91\",\"lon\":\"106\"}]")]
    [InlineData("[{\"place_id\":123,\"display_name\":\"Place\",\"lat\":\"10\",\"lon\":\"NaN\"}]")]
    [InlineData("[{}]")]
    public async Task Invalid_upstream_payload_returns_unavailable_without_retry(string json)
    {
        using var handler = new RecordingHandler(_ => Json(json));
        using var services = CreateServices(handler);
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => Search(services).SearchAsync("Ha Noi", default));
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Rate_limit_sets_cooldown_without_retrying_or_hammering_next_query()
    {
        using var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        });
        using var services = CreateServices(handler);
        var search = Search(services);
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => search.SearchAsync("Ha Noi", default));
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => search.SearchAsync("Sai Gon", default));
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Transient_errors_retry_twice_then_return_success_with_pacing()
    {
        using var handler = new RecordingHandler(attempt => attempt < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(AddressJson));
        using var services = CreateServices(handler);
        Assert.Single(await Search(services).SearchAsync("Ha Noi", default));
        Assert.Equal(3, handler.Urls.Count);
        Assert.All(handler.Timestamps.Zip(handler.Timestamps.Skip(1)), pair =>
            Assert.True(Stopwatch.GetElapsedTime(pair.First, pair.Second) >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Concurrent_queries_are_paced_application_wide()
    {
        using var handler = new RecordingHandler(_ => Json(AddressJson));
        using var services = CreateServices(handler);
        await Task.WhenAll(Search(services).SearchAsync("Ha Noi", default), Search(services).SearchAsync("Sai Gon", default));
        Assert.Equal(2, handler.Urls.Count);
        Assert.True(Stopwatch.GetElapsedTime(handler.Timestamps[0], handler.Timestamps[1]) >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Persistent_failure_stops_after_three_attempts()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var services = CreateServices(handler);
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => Search(services).SearchAsync("Ha Noi", default));
        Assert.Equal(3, handler.Urls.Count);
    }

    [Fact]
    public async Task Permanent_error_is_not_retried()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var services = CreateServices(handler);
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => Search(services).SearchAsync("Ha Noi", default));
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Network_failure_is_retried_and_can_recover()
    {
        using var handler = new RecordingHandler(attempt => attempt == 1
            ? throw new HttpRequestException("Connection failed") : Json(AddressJson));
        using var services = CreateServices(handler);
        Assert.Single(await Search(services).SearchAsync("Ha Noi", default));
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task Oversized_response_is_unavailable_without_retry()
    {
        using var handler = new RecordingHandler(_ => Json(new string('x', 65_537)));
        using var services = CreateServices(handler);
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => Search(services).SearchAsync("Ha Noi", default));
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Http_timeout_is_retried_then_returns_unavailable()
    {
        using var handler = new RecordingHandler(_ => throw new TaskCanceledException("HTTP timeout"));
        using var services = CreateServices(handler);
        await Assert.ThrowsAsync<GeocodingUnavailableException>(() => Search(services).SearchAsync("Ha Noi", default));
        Assert.Equal(3, handler.Urls.Count);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_pacing_does_not_call_upstream()
    {
        using var handler = new RecordingHandler(_ => Json(AddressJson));
        using var services = CreateServices(handler);
        var search = Search(services);
        await search.SearchAsync("Ha Noi", default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchAsync("Sai Gon", cancellation.Token));
        Assert.Single(handler.Urls);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static IAddressSearch Search(ServiceProvider services) => services.GetRequiredService<IAddressSearch>();

    private static ServiceProvider CreateServices(RecordingHandler handler, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddGeocoding(new ConfigurationBuilder().Build());
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddHttpClient("Nominatim").ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<string> UserAgents { get; } = [];
        public List<long> Timestamps { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            Timestamps.Add(Stopwatch.GetTimestamp());
            return Task.FromResult(respond(Urls.Count));
        }
    }
}
