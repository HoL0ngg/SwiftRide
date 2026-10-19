using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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

    [Theory]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Jwt:SigningKey", "too-short")]
    [InlineData("Jwt:SigningKey", "                                ")]
    [InlineData("RateLimit:PermitLimit", "0")]
    [InlineData("RateLimit:WindowSeconds", "0")]
    [InlineData("RateLimit:QueueLimit", "-1")]
    [InlineData("ReverseProxy:Clusters:trip-service:Destinations:primary:Address", "")]
    [InlineData("ReverseProxy:Clusters:trip-service:Destinations:primary:Address", "ftp://invalid-scheme")]
    public void Startup_RejectsInvalidConfiguration(string configKey, string configValue)
    {
        var repoRoot = FindRepoRoot();
        var gatewayContentRoot = Path.Combine(repoRoot, "src", "ApiGateway");

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = gatewayContentRoot,
            ApplicationName = typeof(SwiftRide.ApiGateway.GatewayApplication).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();

        var validConfig = new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "valid-issuer",
            ["Jwt:Audience"] = "valid-audience",
            ["Jwt:SigningKey"] = "a-valid-signing-key-that-is-at-least-32-chars-long",
            ["RateLimit:PermitLimit"] = "100",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = "http://127.0.0.1:5001",
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = "http://127.0.0.1:5002"
        };
        validConfig[configKey] = configValue;
        builder.Configuration.AddInMemoryCollection(validConfig);

        var ex = Assert.Throws<InvalidOperationException>(() => SwiftRide.ApiGateway.GatewayApplication.Build(builder));
        Assert.NotNull(ex.Message);
        Assert.DoesNotContain("a-valid-signing-key", ex.Message);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SwiftRide.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not find repository root containing SwiftRide.sln.");
    }
}
