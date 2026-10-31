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
using Moq;
using SwiftRide.TripService.Api;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using Xunit;

namespace SwiftRide.Api.Tests.Trip;

public class TripEndpointTests
{
    private const string SigningKey = "test-only-trip-signing-key-at-least-32-bytes";
    private const string Issuer = "swiftride-tests";
    private const string Audience = "swiftride-tests";

    private async Task<(WebApplication App, HttpClient Client, MockServices Mocks)> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TripDB"] = "Server=localhost;Database=unused;User Id=sa;Password=unused",
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["Jwt:SigningKey"] = SigningKey,
            ["Messaging:Transport"] = "InMemory"
        });

        var mocks = new MockServices();

        var app = TripApplication.Build(builder, services =>
        {
            services.AddSingleton(mocks.Reader.Object);
            services.AddSingleton(mocks.Writer.Object);
            services.AddSingleton(mocks.Uow.Object);
            services.AddSingleton(mocks.ReceiptStore.Object);
            services.AddSingleton(mocks.AssignmentStore.Object);
            services.AddSingleton(mocks.WorkStore.Object);
            services.AddSingleton(mocks.QueryReader.Object);
            services.AddSingleton(mocks.MatchingClient.Object);
            services.AddSingleton(mocks.PaymentClient.Object);
        });

        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
        return (app, client, mocks);
    }

    private static void Authenticate(HttpClient client, string id, string role)
    {
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            new[] { new Claim("sub", id), new Claim("role", role) },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256));

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    [Fact]
    public async Task CreateTrip_Unauthenticated_Returns401()
    {
        var (app, client, _) = await CreateHostAsync();
        await using (app)
        using (client)
        {
            var req = new CreateTripRequest(
                new GeoPointRequest(10.76, 106.66), "Pickup", "Addr 1",
                new GeoPointRequest(10.78, 106.72), "Dest", "Addr 2",
                "card");

            var res = await client.PostAsJsonAsync("/trips", req);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task CreateTrip_DriverRole_Returns403()
    {
        var (app, client, _) = await CreateHostAsync();
        await using (app)
        using (client)
        {
            Authenticate(client, "drv-1", "driver");

            var req = new CreateTripRequest(
                new GeoPointRequest(10.76, 106.66), "Pickup", "Addr 1",
                new GeoPointRequest(10.78, 106.72), "Dest", "Addr 2",
                "card");

            var res = await client.PostAsJsonAsync("/trips", req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
    }

    [Fact]
    public async Task CreateTrip_MissingIdempotencyKey_Returns400()
    {
        var (app, client, _) = await CreateHostAsync();
        await using (app)
        using (client)
        {
            Authenticate(client, "rider-1", "rider");

            var req = new CreateTripRequest(
                new GeoPointRequest(10.76, 106.66), "Pickup", "Addr 1",
                new GeoPointRequest(10.78, 106.72), "Dest", "Addr 2",
                "card");

            var res = await client.PostAsJsonAsync("/trips", req);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task CreateTrip_Valid_Returns201Created_WithLocationHeader_AndStringFare()
    {
        var (app, client, mocks) = await CreateHostAsync();
        await using (app)
        using (client)
        {
            Authenticate(client, "rider-1", "rider");
            client.DefaultRequestHeaders.Add("Idempotency-Key", "key-trip-create-1");

            mocks.MatchingClient.Setup(m => m.MatchAsync(It.IsAny<MatchInput>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MatchOutcome(MatchStatus.Matched, new CandidateDriver("drv-1", 1.5)));

            var quoteId = Guid.NewGuid();
            mocks.MatchingClient.Setup(m => m.QuoteAsync(It.IsAny<QuoteInput>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QuotedFare(quoteId, 50_000m, "VND", 5.0m, 15.0m, DateTimeOffset.UtcNow));

            var req = new CreateTripRequest(
                new GeoPointRequest(10.76, 106.66), "Ben Thanh", "District 1",
                new GeoPointRequest(10.78, 106.72), "Landmark 81", "Binh Thanh",
                "card");

            var res = await client.PostAsJsonAsync("/trips", req);

            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            Assert.NotNull(res.Headers.Location);

            var body = await res.Content.ReadFromJsonAsync<TripResponse>();
            Assert.NotNull(body);
            Assert.Equal("Quoted", body.Status);
            Assert.Equal("50000", body.FareAmount);
            Assert.Equal("VND", body.Currency);
            Assert.Equal("drv-1", body.CandidateDriverId);
        }
    }

    [Fact]
    public async Task GetCurrentTrip_WhenNoActiveTrip_Returns204NoContent()
    {
        var (app, client, mocks) = await CreateHostAsync();
        await using (app)
        using (client)
        {
            Authenticate(client, "rider-1", "rider");
            mocks.QueryReader.Setup(q => q.GetCurrentAsync(It.IsAny<TripActor>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TripContext?)null);

            var res = await client.GetAsync("/trips/current");
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        }
    }

    private sealed class MockServices
    {
        public Mock<ITripReader> Reader { get; } = new();
        public Mock<ITripWriter> Writer { get; } = new();
        public Mock<ITripUnitOfWork> Uow { get; } = new();
        public Mock<ICommandReceiptStore> ReceiptStore { get; } = new();
        public Mock<IActiveAssignmentStore> AssignmentStore { get; } = new();
        public Mock<ITripWorkStore> WorkStore { get; } = new();
        public Mock<ITripQueryReader> QueryReader { get; } = new();
        public Mock<IMatchingClient> MatchingClient { get; } = new();
        public Mock<IPaymentClient> PaymentClient { get; } = new();
    }
}
