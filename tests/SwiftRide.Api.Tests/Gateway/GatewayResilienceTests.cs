using System.Net;
using System.Net.Http.Headers;

namespace SwiftRide.Api.Tests.Gateway;

public class GatewayResilienceTests
{
    [Fact]
    public async Task Requests_OverFixedWindowLimit_Return429WithoutProxying()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["RateLimit:PermitLimit"] = "2",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0"
        };

        await using var fixture = await GatewayFixture.StartAsync(overrides: overrides);
        var token = fixture.CreateToken("rider-1", "rider");

        // Request 1: Allowed
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/trips");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp1 = await fixture.Client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.NotImplemented, resp1.StatusCode);

        // Request 2: Allowed
        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/trips");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp2 = await fixture.Client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.NotImplemented, resp2.StatusCode);

        // Request 3: Exceeds limit -> 429 Too Many Requests
        using var req3 = new HttpRequestMessage(HttpMethod.Post, "/trips");
        req3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp3 = await fixture.Client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp3.StatusCode);

        // Upstream receives exactly 2 requests; 3rd was rejected at Gateway
        Assert.Equal(2, fixture.Trip.Requests.Count);
    }

    [Fact]
    public async Task StalledMutation_TimesOutWithoutReplay()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:trip-service:HttpRequest:ActivityTimeout"] = "00:00:00.250"
        };

        await using var fixture = await GatewayFixture.StartAsync(stallTripResponse: true, overrides: overrides);
        var token = fixture.CreateToken("rider-1", "rider");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/trips");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);

        // Wait for upstream cancellation
        await fixture.Trip.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));

        // Upstream must only have received 1 request (no mutation replay)
        Assert.Single(fixture.Trip.Requests);
    }
}
