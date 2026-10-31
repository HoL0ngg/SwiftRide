using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SwiftRide.ApiGateway;
using SwiftRide.TripService.Api;
using Xunit;

namespace SwiftRide.Api.Tests.Security;

public class OidcSecurityTests : IAsyncLifetime
{
    private OidcDiscoveryFixture _fixture = default!;

    public async Task InitializeAsync()
    {
        _fixture = new OidcDiscoveryFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    private async Task<(WebApplication App, HttpClient Client)> CreateGatewayClientAsync(
        string? authority = null,
        string? audience = null,
        string environment = "Development",
        string authMode = "Oidc",
        string? signingKey = null,
        int? metadataTimeout = 5)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var configValues = new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = authMode,
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = "http://127.0.0.1:5081/",
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = "http://127.0.0.1:5082/",
            ["ReverseProxy:Routes:trips:ClusterId"] = "trip-service",
            ["ReverseProxy:Routes:trips:Match:Path"] = "/trips/{**catch-all}",
            ["ReverseProxy:Routes:trips:AuthorizationPolicy"] = "public",
            ["RateLimit:PermitLimit"] = "1000",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0"
        };

        if (authMode == "Oidc")
        {
            configValues["Authentication:Authority"] = authority ?? _fixture.AuthorityUrl;
            configValues["Authentication:Audience"] = audience ?? _fixture.DefaultAudience;
            configValues["Authentication:RequireHttpsMetadata"] = "false";
            configValues["Authentication:MetadataTimeoutSeconds"] = metadataTimeout?.ToString() ?? "5";
        }
        else
        {
            configValues["Jwt:Issuer"] = "swiftride-development";
            configValues["Jwt:Audience"] = "swiftride-clients";
            configValues["Jwt:SigningKey"] = signingKey ?? "A_Secure_32_Byte_Development_Key_For_Testing_123456";
        }

        builder.Configuration.AddInMemoryCollection(configValues);

        var app = GatewayApplication.Build(builder);
        await app.StartAsync();
        var url = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
        return (app, client);
    }

    [Fact]
    public async Task ValidOidcToken_AuthenticatesSuccessfully()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            var token = _fixture.CreateToken(sub: "rider-1", role: "rider");
            var request = new HttpRequestMessage(HttpMethod.Get, "/trips/test-id");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidIssuer_Returns401Unauthorized()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            var token = _fixture.CreateToken(sub: "rider-1", role: "rider", issuer: "https://untrusted-issuer.com");
            var request = new HttpRequestMessage(HttpMethod.Get, "/trips/test-id");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidAudience_Returns401Unauthorized()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            var token = _fixture.CreateToken(sub: "rider-1", role: "rider", audience: "wrong-audience");
            var request = new HttpRequestMessage(HttpMethod.Get, "/trips/test-id");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExpiredToken_Returns401Unauthorized()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            var token = _fixture.CreateToken(sub: "rider-1", role: "rider", lifetime: TimeSpan.FromMinutes(-5));
            var request = new HttpRequestMessage(HttpMethod.Get, "/trips/test-id");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidSignature_Returns401Unauthorized()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            using var rogueRsa = RSA.Create(2048);
            var token = _fixture.CreateToken(sub: "rider-1", role: "rider", customKey: rogueRsa);
            var request = new HttpRequestMessage(HttpMethod.Get, "/trips/test-id");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task KeyRotation_RotatedKeyInJwks_AuthenticatesSuccessfully()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            // Rotate key on fixture
            var newKid = _fixture.RotateKey();

            // Create token with rotated key
            var token = _fixture.CreateToken(sub: "rider-rotated", role: "rider", customKid: newKid);
            var request = new HttpRequestMessage(HttpMethod.Get, "/trips/test-id");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void Production_DevelopmentJwtMode_FailsStartup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "DevelopmentJwt",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = "http://trip-service:8080/",
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = "http://matching-service:8080/",
            ["RateLimit:PermitLimit"] = "100",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0"
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            GatewayApplication.Build(builder);
        });

        Assert.Contains("Production environment requires Authentication:Mode='Oidc'", ex.Message);
    }

    [Fact]
    public void Production_OmittedModeWithSigningKey_FailsStartup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Authentication:Mode omitted! But Jwt:SigningKey provided
            ["Jwt:SigningKey"] = "a-very-long-secret-key-that-is-at-least-32-bytes",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = "http://trip-service:8080/",
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = "http://matching-service:8080/",
            ["RateLimit:PermitLimit"] = "100",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0"
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            GatewayApplication.Build(builder);
        });

        Assert.Contains("Production environment requires Authentication:Mode='Oidc'", ex.Message);
    }

    [Fact]
    public void Production_InvalidAuthMode_FailsStartup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "InvalidMode",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = "http://trip-service:8080/",
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = "http://matching-service:8080/",
            ["RateLimit:PermitLimit"] = "100",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0"
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            GatewayApplication.Build(builder);
        });

        Assert.Contains("Invalid Authentication:Mode 'InvalidMode'", ex.Message);
    }

    [Fact]
    public void Production_OidcWithoutHttps_FailsStartup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Oidc",
            ["Authentication:Authority"] = "http://insecure-authority.com",
            ["Authentication:Audience"] = "swiftride-api",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = "http://trip-service:8080/",
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = "http://matching-service:8080/",
            ["RateLimit:PermitLimit"] = "100",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0"
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            GatewayApplication.Build(builder);
        });

        Assert.Contains("OIDC Authority must use HTTPS in Production", ex.Message);
    }

    [Fact]
    public async Task MetadataUnavailable_FailsClosed_Returns401Unauthorized()
    {
        // Point to an unavailable local port with short timeout
        var (app, client) = await CreateGatewayClientAsync(
            authority: "http://127.0.0.1:49999",
            metadataTimeout: 2);
        try
        {
            var token = _fixture.CreateToken(sub: "rider-fail-closed", role: "rider");
            var request = new HttpRequestMessage(HttpMethod.Get, "/trips/test-id");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisallowedRole_FailsAuthorization_Returns403Forbidden()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Oidc",
            ["Authentication:Authority"] = _fixture.AuthorityUrl,
            ["Authentication:Audience"] = _fixture.DefaultAudience,
            ["Authentication:RequireHttpsMetadata"] = "false",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = "http://127.0.0.1:5081/",
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = "http://127.0.0.1:5082/",
            ["ReverseProxy:Routes:driver-only:ClusterId"] = "trip-service",
            ["ReverseProxy:Routes:driver-only:Match:Path"] = "/driver-only",
            ["ReverseProxy:Routes:driver-only:AuthorizationPolicy"] = "driver",
            ["RateLimit:PermitLimit"] = "1000",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0"
        });

        var gatewayApp = GatewayApplication.Build(builder);
        await gatewayApp.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(gatewayApp.Urls.First()) };

        try
        {
            // Token with role "admin" (not driver)
            var tokenAdmin = _fixture.CreateToken(sub: "admin-1", role: "admin");
            var requestAdmin = new HttpRequestMessage(HttpMethod.Get, "/driver-only");
            requestAdmin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);

            var responseAdmin = await client.SendAsync(requestAdmin);
            Assert.Equal(HttpStatusCode.Forbidden, responseAdmin.StatusCode);

            // Token with role "rider" (not driver)
            var tokenRider = _fixture.CreateToken(sub: "rider-1", role: "rider");
            var requestRider = new HttpRequestMessage(HttpMethod.Get, "/driver-only");
            requestRider.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenRider);

            var responseRider = await client.SendAsync(requestRider);
            Assert.Equal(HttpStatusCode.Forbidden, responseRider.StatusCode);

            // Token with role "driver" (authorized)
            var tokenDriver = _fixture.CreateToken(sub: "driver-1", role: "driver");
            var requestDriver = new HttpRequestMessage(HttpMethod.Get, "/driver-only");
            requestDriver.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenDriver);

            var responseDriver = await client.SendAsync(requestDriver);
            Assert.NotEqual(HttpStatusCode.Forbidden, responseDriver.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, responseDriver.StatusCode);
        }
        finally
        {
            client.Dispose();
            await gatewayApp.StopAsync();
            await gatewayApp.DisposeAsync();
        }
    }
}
