using System.Net;
using System.Net.Http.Headers;

namespace SwiftRide.Api.Tests.Gateway;

public class GatewayStartupTests
{
    [Fact]
    public async Task Health_UsesRealGatewayPipeline_Returns200()
    {
        await using var fixture = await GatewayFixture.StartAsync();

        using var response = await fixture.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(fixture.Trip.Requests);
        Assert.Empty(fixture.Matching.Requests);
    }

    [Fact]
    public async Task ValidTripRequest_ReachesConfiguredTripUpstream()
    {
        await using var fixture = await GatewayFixture.StartAsync();

        var token = fixture.CreateToken("rider-1", "rider");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/trips");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Upstream", out var values));
        Assert.Contains("trip", values);

        var upstreamRequest = Assert.Single(fixture.Trip.Requests);
        Assert.Equal("POST", upstreamRequest.Method);
        Assert.Equal("/trips", upstreamRequest.Path);
        Assert.True(upstreamRequest.HasAuthorization);
        Assert.Empty(fixture.Matching.Requests);
    }
}
