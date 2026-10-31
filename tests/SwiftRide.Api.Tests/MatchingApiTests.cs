using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.MatchingService.Api;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Application.Geocoding;

namespace SwiftRide.Api.Tests;

public class MatchingApiTests
{
    [Fact]
    public async Task Real_jwt_ownership_validation_quote_and_search_contracts()
    {
        const string signingKey = "test-only-matching-signing-key-at-least-32-bytes";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MatchDB"] = "Host=localhost;Database=unused;Username=unused;Password=unused;SSL Mode=VerifyFull;Timeout=15;Command Timeout=30",
            ["Security:InternalApiKey"] = "test-internal-matching-key",
            ["Jwt:Issuer"] = "matching-tests", ["Jwt:Audience"] = "matching-tests", ["Jwt:SigningKey"] = signingKey
        });
        var store = new MemoryStore();
        await using var app = MatchingApplication.Build(builder, services =>
        {
            services.AddSingleton<IDriverLocationReader>(store);
            services.AddSingleton<IDriverLocationWriter>(store);
            services.AddSingleton<IQuoteWriter>(store);
            services.AddSingleton<IAddressSearch>(store);
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
        var body = new { latitude = 10.77, longitude = 106.7, isOnline = true };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/drivers/driver-1/location", body)).StatusCode);
        void Authenticate(string id, string role)
        {
            var token = new JwtSecurityToken("matching-tests", "matching-tests", [new("sub", id), new("role", role)],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        }
        Authenticate("driver-1", "rider");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/drivers/driver-1/location", body)).StatusCode);
        Authenticate("driver-1", "driver");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/drivers/other/location", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/drivers/driver-1/location", body)).StatusCode);
        Assert.Single(store.Locations);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/drivers/driver-1/location", new { longitude = 1, isOnline = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/drivers/driver-1/location", new { latitude = 91, longitude = 1, isOnline = true })).StatusCode);
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "test-internal-matching-key");
        var match = await client.PostAsJsonAsync("/match", new { latitude = 10.77, longitude = 106.7 });
        Assert.Equal("driver-1", (await match.Content.ReadFromJsonAsync<MatchResult>())!.Candidate!.DriverId);
        var quote = await client.PostAsJsonAsync("/quote", new { pickup = new { latitude = 10.77, longitude = 106.7 }, destination = new { latitude = 10.78, longitude = 106.71 } });
        Assert.Equal(HttpStatusCode.Created, quote.StatusCode);
        Assert.Single(store.Quotes);
        Assert.Equal(store.Quotes[0].Id, (await quote.Content.ReadFromJsonAsync<QuoteResult>())!.QuoteId);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/quote", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/locations/search?query=x")).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<AddressResult[]>("/locations/search?query=market"))!);
        store.SearchUnavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/locations/search?query=market")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/locations/search?query=market")).StatusCode);
        await app.StopAsync();
    }

    private sealed class MemoryStore : IDriverLocationReader, IDriverLocationWriter, IQuoteWriter, IAddressSearch
    {
        public List<DriverLocation> Locations { get; } = [];
        public List<QuoteSnapshot> Quotes { get; } = [];
        public bool SearchUnavailable { get; set; }
        public Task<IReadOnlyList<DriverLocation>> ReadAvailableAsync(DateTimeOffset since, CancellationToken ct) => Task.FromResult<IReadOnlyList<DriverLocation>>(Locations);
        public Task UpsertAsync(DriverLocation location, CancellationToken ct) { Locations.Add(location); return Task.CompletedTask; }
        public Task AddAsync(QuoteSnapshot quote, CancellationToken ct) { Quotes.Add(quote); return Task.CompletedTask; }
        public Task<IReadOnlyList<AddressResult>> SearchAsync(string query, CancellationToken ct) => SearchUnavailable
            ? throw new GeocodingUnavailableException() : Task.FromResult<IReadOnlyList<AddressResult>>([new("1", "Market", 10.77, 106.7)]);
    }
}
