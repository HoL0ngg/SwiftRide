using System.Net;
using System.Net.Http.Headers;

namespace SwiftRide.Api.Tests.Gateway;

public class GatewayProxyTests
{
    [Fact]
    public async Task PostTrips_WithValidToken_ProxiesToTripService()
    {
        await using var fixture = await GatewayFixture.StartAsync();

        var token = fixture.CreateToken("rider-1", "rider");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/trips");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Upstream", out var values));
        Assert.Contains("trip", values);

        var req = Assert.Single(fixture.Trip.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/trips", req.Path);
        Assert.True(req.HasAuthorization);
        Assert.Empty(fixture.Matching.Requests);
    }

    [Fact]
    public async Task GetTrip_WithQuery_ProxiesMethodPathAndQueryToTripService()
    {
        await using var fixture = await GatewayFixture.StartAsync();

        var token = fixture.CreateToken("rider-1", "rider");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/trips/trip-1?view=summary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Upstream", out var values));
        Assert.Contains("trip", values);

        var req = Assert.Single(fixture.Trip.Requests);
        Assert.Equal("GET", req.Method);
        Assert.Equal("/trips/trip-1", req.Path);
        Assert.Equal("?view=summary", req.QueryString);
        Assert.True(req.HasAuthorization);
        Assert.Empty(fixture.Matching.Requests);
    }

    [Theory]
    [InlineData("confirm-quote")]
    [InlineData("cancel")]
    [InlineData("driver-accept")]
    [InlineData("driver-reject")]
    [InlineData("en-route")]
    [InlineData("pickup")]
    [InlineData("dropoff")]
    public async Task TripActions_WithValidToken_ProxyToTripServiceWithAuth(string action)
    {
        await using var fixture = await GatewayFixture.StartAsync();

        var token = fixture.CreateToken("driver-1", "driver");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/trips/trip-1/{action}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Upstream", out var values));
        Assert.Contains("trip", values);

        var req = Assert.Single(fixture.Trip.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal($"/trips/trip-1/{action}", req.Path);
        Assert.True(req.HasAuthorization);
        Assert.Empty(fixture.Matching.Requests);
    }

    [Fact]
    public async Task PutDriverLocation_WithValidToken_ProxiesToMatchingService()
    {
        await using var fixture = await GatewayFixture.StartAsync();

        var token = fixture.CreateToken("driver-1", "driver");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/drivers/driver-1/location");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Upstream", out var values));
        Assert.Contains("matching", values);

        var req = Assert.Single(fixture.Matching.Requests);
        Assert.Equal("PUT", req.Method);
        Assert.Equal("/drivers/driver-1/location", req.Path);
        Assert.True(req.HasAuthorization);
        Assert.Empty(fixture.Trip.Requests);
    }

    [Fact]
    public async Task AddressSearch_RequiresJwt_AndProxiesOnlyToMatching()
    {
        using var logs = new CapturedLogs();
        await using var fixture = await GatewayFixture.StartAsync(loggerProvider: logs);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/locations/search?query=market")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/locations/search?query=market");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.CreateToken("rider-1", "rider"));
        Assert.Equal(HttpStatusCode.NotImplemented, (await fixture.Client.SendAsync(request)).StatusCode);
        var forwarded = Assert.Single(fixture.Matching.Requests);
        Assert.Equal("/locations/search", forwarded.Path);
        Assert.True(forwarded.HasAuthorization);
        Assert.Empty(fixture.Trip.Requests);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("query=market", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task DriverLocation_NonPutMethods_Return405WithoutProxying(string method)
    {
        await using var fixture = await GatewayFixture.StartAsync();

        var token = fixture.CreateToken("driver-1", "driver");
        using var request = new HttpRequestMessage(new HttpMethod(method), "/drivers/driver-1/location");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(fixture.Matching.Requests);
        Assert.Empty(fixture.Trip.Requests);
    }

    [Theory]
    [InlineData("/match")]
    [InlineData("/quote")]
    [InlineData("/payments")]
    [InlineData("/payments/payment-1/refund")]
    public async Task InternalRoutes_Return404_EvenWithValidToken(string path)
    {
        await using var fixture = await GatewayFixture.StartAsync();

        var token = fixture.CreateToken("rider-1", "rider");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fixture.Matching.Requests);
        Assert.Empty(fixture.Trip.Requests);
    }

    [Theory]
    [InlineData("POST", "/trips")]
    [InlineData("PUT", "/drivers/driver-1/location")]
    public async Task PublicRoutes_WithoutAuth_Return401WithoutProxying(string method, string path)
    {
        await using var fixture = await GatewayFixture.StartAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(fixture.Matching.Requests);
        Assert.Empty(fixture.Trip.Requests);
    }
}
