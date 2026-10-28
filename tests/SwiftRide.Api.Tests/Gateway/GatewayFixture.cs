using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.ApiGateway;

namespace SwiftRide.Api.Tests.Gateway;

public sealed class GatewayFixture : IAsyncDisposable
{
    private readonly WebApplication _gatewayApp;

    private GatewayFixture(
        WebApplication gatewayApp,
        HttpClient client,
        UpstreamStub trip,
        UpstreamStub matching,
        string issuer,
        string audience,
        byte[] signingKey)
    {
        _gatewayApp = gatewayApp;
        Client = client;
        Trip = trip;
        Matching = matching;
        Issuer = issuer;
        Audience = audience;
        SigningKey = signingKey;
    }

    public HttpClient Client { get; }
    public UpstreamStub Trip { get; }
    public UpstreamStub Matching { get; }
    public string Issuer { get; }
    public string Audience { get; }
    public byte[] SigningKey { get; }

    public static async Task<GatewayFixture> StartAsync(
        string environment = "Development",
        bool stallTripResponse = false,
        IReadOnlyDictionary<string, string?>? overrides = null,
        CancellationToken cancellationToken = default,
        ILoggerProvider? loggerProvider = null)
    {
        UpstreamStub? tripStub = null;
        UpstreamStub? matchingStub = null;
        WebApplication? gatewayApp = null;
        HttpClient? client = null;

        try
        {
            tripStub = await UpstreamStub.StartAsync("trip", stallResponse: stallTripResponse, cancellationToken: cancellationToken);
            matchingStub = await UpstreamStub.StartAsync("matching", stallResponse: false, cancellationToken: cancellationToken);

            var repoRoot = FindRepoRoot();
            var gatewayContentRoot = Path.Combine(repoRoot, "src", "ApiGateway");

            var issuer = "swiftride-test-issuer";
            var audience = "swiftride-test-audience";

            var randomBytes = new byte[32];
            RandomNumberGenerator.Fill(randomBytes);
            var signingKeyStr = Convert.ToBase64String(randomBytes);
            var signingKeyBytes = Encoding.UTF8.GetBytes(signingKeyStr);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environment,
                ContentRootPath = gatewayContentRoot,
                ApplicationName = typeof(GatewayApplication).Assembly.GetName().Name
            });

            builder.Logging.ClearProviders();
            if (loggerProvider is not null) builder.Logging.AddProvider(loggerProvider);
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var inMemoryConfig = new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = issuer,
                ["Jwt:Audience"] = audience,
                ["Jwt:SigningKey"] = signingKeyStr,
                ["RateLimit:PermitLimit"] = "100",
                ["RateLimit:WindowSeconds"] = "60",
                ["RateLimit:QueueLimit"] = "0",
                ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = tripStub.Address.ToString(),
                ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = matchingStub.Address.ToString()
            };

            if (overrides != null)
            {
                foreach (var (k, v) in overrides)
                {
                    inMemoryConfig[k] = v;
                }
            }

            builder.Configuration.AddInMemoryCollection(inMemoryConfig);

            gatewayApp = GatewayApplication.Build(builder);
            await gatewayApp.StartAsync(cancellationToken);

            var effectiveIssuer = gatewayApp.Configuration["Jwt:Issuer"] ?? issuer;
            var effectiveAudience = gatewayApp.Configuration["Jwt:Audience"] ?? audience;
            var effectiveKeyStr = gatewayApp.Configuration["Jwt:SigningKey"] ?? signingKeyStr;
            var effectiveKeyBytes = Encoding.UTF8.GetBytes(effectiveKeyStr);

            client = new HttpClient
            {
                BaseAddress = new Uri(gatewayApp.Urls.Single()),
                Timeout = TimeSpan.FromSeconds(5)
            };

            return new GatewayFixture(
                gatewayApp,
                client,
                tripStub,
                matchingStub,
                effectiveIssuer,
                effectiveAudience,
                effectiveKeyBytes);
        }
        catch
        {
            client?.Dispose();
            if (gatewayApp != null)
            {
                try { await gatewayApp.StopAsync(); } catch { }
                await gatewayApp.DisposeAsync();
            }
            if (matchingStub != null) await matchingStub.DisposeAsync();
            if (tripStub != null) await tripStub.DisposeAsync();
            throw;
        }
    }

    public string CreateToken(string subject, string role)
    {
        var key = new SymmetricSecurityKey(SigningKey);
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, subject), new Claim("role", role)],
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await _gatewayApp.StopAsync();
        }
        catch
        {
            // Ignore shutdown errors
        }
        await _gatewayApp.DisposeAsync();
        await Matching.DisposeAsync();
        await Trip.DisposeAsync();
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
