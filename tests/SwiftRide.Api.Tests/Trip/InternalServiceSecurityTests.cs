using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SwiftRide.MatchingService.Api;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Application.Geocoding;
using SwiftRide.PaymentService.Api;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Repositories;
using Xunit;

namespace SwiftRide.Api.Tests.Trip;

public class InternalServiceSecurityTests
{
    private const string MatchingKey = "secret-matching-key";
    private const string PaymentKey = "secret-payment-key";

    private async Task<(WebApplication App, HttpClient Client)> CreateMatchingHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MatchDB"] = "Host=localhost;Database=unused;Username=unused;Password=unused;SSL Mode=VerifyFull;Timeout=15;Command Timeout=30",
            ["Matching:InternalApiKey"] = MatchingKey,
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
            ["Jwt:SigningKey"] = "test-signing-key-that-is-at-least-32-chars-long"
        });

        var mockReader = new Mock<IDriverLocationReader>();
        mockReader.Setup(r => r.ReadAvailableAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DriverLocation>());

        var mockWriter = new Mock<IDriverLocationWriter>();
        var mockQuoteWriter = new Mock<IQuoteWriter>();
        var mockSearch = new Mock<IAddressSearch>();

        var app = MatchingApplication.Build(builder, services =>
        {
            services.AddSingleton(mockReader.Object);
            services.AddSingleton(mockWriter.Object);
            services.AddSingleton(mockQuoteWriter.Object);
            services.AddSingleton(mockSearch.Object);
        });

        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        return (app, client);
    }

    private async Task<(WebApplication App, HttpClient Client)> CreatePaymentHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PaymentDB"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["Payment:InternalApiKey"] = PaymentKey,
            ["Messaging:Transport"] = "InMemory"
        });

        var mockReader = new Mock<IPaymentReader>();
        var mockWriter = new Mock<IPaymentWriter>();
        var mockUow = new Mock<IUnitOfWork>();
        var mockProcessing = new Mock<IPaymentProcessingStore>();
        var mockOperations = new Mock<IMockOperationStore>();
        var mockGatewayFactory = new Mock<IPaymentGatewayFactory>();

        var app = PaymentApplication.Build(builder, services =>
        {
            services.AddSingleton(mockReader.Object);
            services.AddSingleton(mockWriter.Object);
            services.AddSingleton(mockUow.Object);
            services.AddSingleton(mockProcessing.Object);
            services.AddSingleton(mockOperations.Object);
            services.AddSingleton(mockGatewayFactory.Object);
        });

        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        return (app, client);
    }

    [Fact]
    public async Task MatchingInternalEndpoints_RequireInternalApiKey()
    {
        var (app, client) = await CreateMatchingHostAsync();
        await using (app)
        using (client)
        {
            // Health works without key
            var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            // POST /match without key -> 401
            var matchNoKey = await client.PostAsJsonAsync("/match", new { latitude = 10.77, longitude = 106.7 });
            Assert.Equal(HttpStatusCode.Unauthorized, matchNoKey.StatusCode);

            // POST /match with wrong key -> 401
            client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "wrong-key");
            var matchWrongKey = await client.PostAsJsonAsync("/match", new { latitude = 10.77, longitude = 106.7 });
            Assert.Equal(HttpStatusCode.Unauthorized, matchWrongKey.StatusCode);

            // POST /match with correct key -> 200
            client.DefaultRequestHeaders.Remove("X-Internal-Api-Key");
            client.DefaultRequestHeaders.Add("X-Internal-Api-Key", MatchingKey);
            var matchCorrectKey = await client.PostAsJsonAsync("/match", new { latitude = 10.77, longitude = 106.7 });
            Assert.Equal(HttpStatusCode.OK, matchCorrectKey.StatusCode);

            // POST /quote with correct key -> 201
            var quote = await client.PostAsJsonAsync("/quote", new
            {
                pickup = new { latitude = 10.77, longitude = 106.7 },
                destination = new { latitude = 10.78, longitude = 106.71 }
            });
            Assert.Equal(HttpStatusCode.Created, quote.StatusCode);
        }
    }

    [Fact]
    public async Task PaymentInternalEndpoints_RequireInternalApiKey()
    {
        var (app, client) = await CreatePaymentHostAsync();
        await using (app)
        using (client)
        {
            // Health works without key
            var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var tripId = Guid.NewGuid();

            // GET /payments by trip without key -> 401
            var getNoKey = await client.GetAsync($"/payments?tripId={tripId}");
            Assert.Equal(HttpStatusCode.Unauthorized, getNoKey.StatusCode);

            // With wrong key -> 401
            client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "wrong-key");
            var getWrongKey = await client.GetAsync($"/payments?tripId={tripId}");
            Assert.Equal(HttpStatusCode.Unauthorized, getWrongKey.StatusCode);

            // With correct key -> 200/404 (not 401!)
            client.DefaultRequestHeaders.Remove("X-Internal-Api-Key");
            client.DefaultRequestHeaders.Add("X-Internal-Api-Key", PaymentKey);
            var getCorrectKey = await client.GetAsync($"/payments?tripId={tripId}");
            Assert.True(getCorrectKey.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);
        }
    }
}
