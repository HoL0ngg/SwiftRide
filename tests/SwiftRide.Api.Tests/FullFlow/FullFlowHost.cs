using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.ApiGateway;
using SwiftRide.MatchingService.Api;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Application.Geocoding;
using SwiftRide.MatchingService.Domain;
using SwiftRide.MatchingService.Infrastructure.Persistence;
using SwiftRide.PaymentService.Api;
using SwiftRide.PaymentService.Infrastructure.Persistence;
using SwiftRide.TripService.Api;
using SwiftRide.TripService.Infrastructure.Persistence;
using Xunit;

namespace SwiftRide.Api.Tests.FullFlow;

public sealed class LocalFullFlowFactAttribute : FactAttribute
{
    public LocalFullFlowFactAttribute()
    {
        var tripDb = Environment.GetEnvironmentVariable("TRIP_TEST_DB");
        var payDb = Environment.GetEnvironmentVariable("PAYMENT_TEST_DB");
        if (string.IsNullOrEmpty(tripDb) || string.IsNullOrEmpty(payDb))
        {
            Skip = "Set TRIP_TEST_DB and PAYMENT_TEST_DB to run FullFlow end-to-end integration tests.";
        }
    }
}

public sealed class FullFlowMatchingStore : IDriverLocationReader, IDriverLocationWriter, IQuoteWriter, IAddressSearch
{
    private readonly List<DriverLocation> _locations = [];
    private readonly List<QuoteSnapshot> _quotes = [];

    public Task<IReadOnlyList<DriverLocation>> ReadAvailableAsync(DateTimeOffset since, CancellationToken ct)
    {
        lock (_locations)
        {
            var active = _locations
                .Where(l => l.IsOnline && l.UpdatedAt >= since)
                .GroupBy(l => l.DriverId)
                .Select(g => g.OrderByDescending(x => x.UpdatedAt).First())
                .ToList();
            return Task.FromResult<IReadOnlyList<DriverLocation>>(active);
        }
    }

    public Task UpsertAsync(DriverLocation location, CancellationToken ct)
    {
        lock (_locations)
        {
            _locations.RemoveAll(l => l.DriverId == location.DriverId);
            _locations.Add(location);
        }
        return Task.CompletedTask;
    }

    public Task AddAsync(QuoteSnapshot quote, CancellationToken ct)
    {
        lock (_quotes)
        {
            _quotes.Add(quote);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AddressResult>> SearchAsync(string query, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<AddressResult>>([
            new("1", "Chợ Bến Thành, Quận 1", 10.7725, 106.6980),
            new("2", "Landmark 81, Bình Thạnh", 10.7951, 106.7219)
        ]);
    }
}

public sealed class FullFlowHost : IAsyncDisposable
{
    public const string TestIssuer = "swiftride-fullflow-test";
    public const string TestAudience = "swiftride-fullflow-test";
    public const string TestSigningKey = "super-secret-signing-key-for-fullflow-tests-32bytes!";
    public const string InternalMatchingKey = "internal-matching-key-fullflow-123";
    public const string InternalPaymentKey = "internal-payment-key-fullflow-123";

    public WebApplication GatewayApp { get; }
    public WebApplication TripApp { get; }
    public WebApplication MatchingApp { get; }
    public WebApplication PaymentApp { get; }
    public HttpClient GatewayClient { get; }
    public string TripDbConnectionString { get; }
    public string PaymentDbConnectionString { get; }
    public string? MatchingDbConnectionString { get; }
    public FullFlowMatchingStore? MatchingStore { get; }

    private FullFlowHost(
        WebApplication gatewayApp,
        WebApplication tripApp,
        WebApplication matchingApp,
        WebApplication paymentApp,
        HttpClient gatewayClient,
        string tripDb,
        string paymentDb,
        FullFlowMatchingStore? matchingStore,
        string? matchingDb = null)
    {
        GatewayApp = gatewayApp;
        TripApp = tripApp;
        MatchingApp = matchingApp;
        PaymentApp = paymentApp;
        GatewayClient = gatewayClient;
        TripDbConnectionString = tripDb;
        PaymentDbConnectionString = paymentDb;
        MatchingStore = matchingStore;
        MatchingDbConnectionString = matchingDb;
    }

    public static async Task<FullFlowHost> StartAsync(
        Action<IServiceCollection>? configureTrip = null,
        Action<IServiceCollection>? configurePayment = null,
        Action<IServiceCollection>? configureMatching = null)
    {
        var tripDb = Environment.GetEnvironmentVariable("TRIP_TEST_DB");
        var paymentDb = Environment.GetEnvironmentVariable("PAYMENT_TEST_DB");
        if (string.IsNullOrEmpty(tripDb) || string.IsNullOrEmpty(paymentDb))
        {
            throw new InvalidOperationException("TRIP_TEST_DB and PAYMENT_TEST_DB environment variables are required to run FullFlowHost.");
        }

        var sqlBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(tripDb);
        var sqlHost = sqlBuilder.DataSource.Split(',')[0].Trim();
        if (sqlHost is not ("localhost" or "127.0.0.1") || !sqlBuilder.InitialCatalog.Equals("swiftride_trip_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FullFlow SQL Server test DB must run on localhost and be 'swiftride_trip_test'. Detected: {tripDb}");
        }

        var pgBuilder = new Npgsql.NpgsqlConnectionStringBuilder(paymentDb);
        if (pgBuilder.Host is not ("localhost" or "127.0.0.1") || !string.Equals(pgBuilder.Database, "swiftride_payment_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FullFlow PostgreSQL test DB must run on localhost and be 'swiftride_payment_test'. Detected: {paymentDb}");
        }

        // 1. Build and start Matching Service
        var matchingStore = new FullFlowMatchingStore();
        var matchingBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        matchingBuilder.Logging.ClearProviders();
        matchingBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        matchingBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MatchDB"] = "Host=localhost;Database=unused;Username=unused;Password=unused;SSL Mode=VerifyFull;Timeout=15;Command Timeout=30",
            ["Jwt:Issuer"] = TestIssuer,
            ["Jwt:Audience"] = TestAudience,
            ["Jwt:SigningKey"] = TestSigningKey,
            ["Security:InternalApiKey"] = InternalMatchingKey,
            ["Matching:SeedDemoDrivers"] = "false"
        });
        var matchingApp = MatchingApplication.Build(matchingBuilder, services =>
        {
            services.AddSingleton<IDriverLocationReader>(matchingStore);
            services.AddSingleton<IDriverLocationWriter>(matchingStore);
            services.AddSingleton<IQuoteWriter>(matchingStore);
            services.AddSingleton<IAddressSearch>(matchingStore);
            configureMatching?.Invoke(services);
        });
        await matchingApp.StartAsync();
        var matchingUrl = matchingApp.Urls.First();

        // 2. Build and start Payment Service
        var paymentBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        paymentBuilder.Logging.ClearProviders();
        paymentBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        paymentBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PaymentDB"] = paymentDb,
            ["Security:InternalApiKey"] = InternalPaymentKey,
            ["Messaging:Transport"] = "InMemory"
        });
        var paymentApp = PaymentApplication.Build(paymentBuilder, configurePayment);
        await paymentApp.StartAsync();
        var paymentUrl = paymentApp.Urls.First();

        // 3. Build and start Trip Service
        var tripBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        tripBuilder.Logging.ClearProviders();
        tripBuilder.Logging.AddConsole();
        tripBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        tripBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TripDB"] = tripDb,
            ["Jwt:Issuer"] = TestIssuer,
            ["Jwt:Audience"] = TestAudience,
            ["Jwt:SigningKey"] = TestSigningKey,
            ["Services:Matching:BaseAddress"] = matchingUrl,
            ["Services:Matching:ApiKey"] = InternalMatchingKey,
            ["Services:Payment:BaseAddress"] = paymentUrl,
            ["Services:Payment:ApiKey"] = InternalPaymentKey,
            ["MatchingService:BaseUrl"] = matchingUrl,
            ["MatchingService:InternalApiKey"] = InternalMatchingKey,
            ["PaymentService:BaseUrl"] = paymentUrl,
            ["PaymentService:InternalApiKey"] = InternalPaymentKey,
            ["PaymentWorkflow:InitialAttemptTimeoutSeconds"] = "10",
            ["Messaging:Transport"] = "InMemory"
        });
        var tripApp = TripApplication.Build(tripBuilder, configureTrip);
        await tripApp.StartAsync();
        var tripUrl = tripApp.Urls.First();

        // 4. Build and start Gateway
        var gatewayBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        gatewayBuilder.Logging.ClearProviders();
        gatewayBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        gatewayBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = TestIssuer,
            ["Jwt:Audience"] = TestAudience,
            ["Jwt:SigningKey"] = TestSigningKey,
            ["ReverseProxy:Routes:trips:ClusterId"] = "trip-service",
            ["ReverseProxy:Routes:trips:AuthorizationPolicy"] = "public",
            ["ReverseProxy:Routes:trips:Match:Path"] = "/trips/{**catch-all}",
            ["ReverseProxy:Routes:driver-location:ClusterId"] = "matching-service",
            ["ReverseProxy:Routes:driver-location:AuthorizationPolicy"] = "public",
            ["ReverseProxy:Routes:driver-location:Match:Path"] = "/drivers/{id}/location",
            ["ReverseProxy:Routes:driver-location:Match:Methods:0"] = "PUT",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = tripUrl,
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = matchingUrl,
            ["RateLimit:PermitLimit"] = "1000",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "100"
        });
        var gatewayApp = GatewayApplication.Build(gatewayBuilder);
        await gatewayApp.StartAsync();
        var gatewayUrl = gatewayApp.Urls.First();

        var gatewayClient = new HttpClient { BaseAddress = new Uri(gatewayUrl), Timeout = TimeSpan.FromSeconds(30) };

        return new FullFlowHost(gatewayApp, tripApp, matchingApp, paymentApp, gatewayClient, tripDb, paymentDb, matchingStore);
    }

    public static async Task<FullFlowHost> StartPersistentAsync(
        Action<IServiceCollection>? configureTrip = null,
        Action<IServiceCollection>? configurePayment = null,
        Action<IServiceCollection>? configureMatching = null,
        CancellationToken ct = default)
    {
        var tripDb = Environment.GetEnvironmentVariable("TRIP_TEST_DB");
        var paymentDb = Environment.GetEnvironmentVariable("PAYMENT_TEST_DB");
        var matchingDb = Environment.GetEnvironmentVariable("MATCHING_TEST_DB");
        if (string.IsNullOrEmpty(tripDb) || string.IsNullOrEmpty(paymentDb) || string.IsNullOrEmpty(matchingDb))
        {
            throw new InvalidOperationException("TRIP_TEST_DB, PAYMENT_TEST_DB, and MATCHING_TEST_DB environment variables are required to run persistent FullFlowHost.");
        }

        var sqlBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(tripDb);
        var sqlHost = sqlBuilder.DataSource.Split(',')[0].Trim();
        if (sqlHost is not ("localhost" or "127.0.0.1") || !sqlBuilder.InitialCatalog.Equals("swiftride_trip_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FullFlow SQL Server test DB must run on localhost and be 'swiftride_trip_test'. Detected: {tripDb}");
        }

        var pgBuilder = new Npgsql.NpgsqlConnectionStringBuilder(paymentDb);
        if (pgBuilder.Host is not ("localhost" or "127.0.0.1") || !string.Equals(pgBuilder.Database, "swiftride_payment_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FullFlow PostgreSQL test DB must run on localhost and be 'swiftride_payment_test'. Detected: {paymentDb}");
        }

        var matchBuilder = new Npgsql.NpgsqlConnectionStringBuilder(matchingDb);
        if (matchBuilder.Host is not ("localhost" or "127.0.0.1") || !string.Equals(matchBuilder.Database, "matching_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FullFlow PostgreSQL test DB must run on localhost and be 'matching_test'. Detected: {matchingDb}");
        }

        // 1. Build and start Matching Service with real PostgreSQL MatchDB
        var matchingBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        matchingBuilder.Logging.ClearProviders();
        matchingBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        matchingBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MatchDB"] = matchingDb,
            ["Jwt:Issuer"] = TestIssuer,
            ["Jwt:Audience"] = TestAudience,
            ["Jwt:SigningKey"] = TestSigningKey,
            ["Security:InternalApiKey"] = InternalMatchingKey,
            ["Matching:SeedDemoDrivers"] = "false"
        });
        var matchingApp = MatchingApplication.Build(matchingBuilder, configureMatching);
        await matchingApp.StartAsync(ct);
        var matchingUrl = matchingApp.Urls.First();

        // 2. Build and start Payment Service with real PostgreSQL PaymentDB
        var paymentBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        paymentBuilder.Logging.ClearProviders();
        paymentBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        paymentBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PaymentDB"] = paymentDb,
            ["Security:InternalApiKey"] = InternalPaymentKey,
            ["Messaging:Transport"] = "InMemory"
        });
        var paymentApp = PaymentApplication.Build(paymentBuilder, configurePayment);
        await paymentApp.StartAsync(ct);
        var paymentUrl = paymentApp.Urls.First();

        // 3. Build and start Trip Service with real SQL Server TripDB
        var tripBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        tripBuilder.Logging.ClearProviders();
        tripBuilder.Logging.AddConsole();
        tripBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        tripBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TripDB"] = tripDb,
            ["Jwt:Issuer"] = TestIssuer,
            ["Jwt:Audience"] = TestAudience,
            ["Jwt:SigningKey"] = TestSigningKey,
            ["Services:Matching:BaseAddress"] = matchingUrl,
            ["Services:Matching:ApiKey"] = InternalMatchingKey,
            ["Services:Payment:BaseAddress"] = paymentUrl,
            ["Services:Payment:ApiKey"] = InternalPaymentKey,
            ["MatchingService:BaseUrl"] = matchingUrl,
            ["MatchingService:InternalApiKey"] = InternalMatchingKey,
            ["PaymentService:BaseUrl"] = paymentUrl,
            ["PaymentService:InternalApiKey"] = InternalPaymentKey,
            ["PaymentWorkflow:InitialAttemptTimeoutSeconds"] = "10",
            ["Messaging:Transport"] = "InMemory"
        });
        var tripApp = TripApplication.Build(tripBuilder, configureTrip);
        await tripApp.StartAsync(ct);
        var tripUrl = tripApp.Urls.First();

        // 4. Build and start Gateway
        var gatewayBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        gatewayBuilder.Logging.ClearProviders();
        gatewayBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        gatewayBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = TestIssuer,
            ["Jwt:Audience"] = TestAudience,
            ["Jwt:SigningKey"] = TestSigningKey,
            ["ReverseProxy:Routes:trips:ClusterId"] = "trip-service",
            ["ReverseProxy:Routes:trips:AuthorizationPolicy"] = "public",
            ["ReverseProxy:Routes:trips:Match:Path"] = "/trips/{**catch-all}",
            ["ReverseProxy:Routes:driver-location:ClusterId"] = "matching-service",
            ["ReverseProxy:Routes:driver-location:AuthorizationPolicy"] = "public",
            ["ReverseProxy:Routes:driver-location:Match:Path"] = "/drivers/{id}/location",
            ["ReverseProxy:Routes:driver-location:Match:Methods:0"] = "PUT",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = tripUrl,
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = matchingUrl,
            ["RateLimit:PermitLimit"] = "1000",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "100"
        });
        var gatewayApp = GatewayApplication.Build(gatewayBuilder);
        await gatewayApp.StartAsync(ct);
        var gatewayUrl = gatewayApp.Urls.First();

        var gatewayClient = new HttpClient { BaseAddress = new Uri(gatewayUrl), Timeout = TimeSpan.FromSeconds(30) };

        return new FullFlowHost(gatewayApp, tripApp, matchingApp, paymentApp, gatewayClient, tripDb, paymentDb, matchingStore: null, matchingDb: matchingDb);
    }

    public string CreateToken(string userId, string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: [new Claim("sub", userId), new Claim("role", role)],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TripDbContext CreateTripDbContext()
    {
        var options = new DbContextOptionsBuilder<TripDbContext>()
            .UseSqlServer(TripDbConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new TripDbContext(options);
    }

    public PaymentDbContext CreatePaymentDbContext()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(PaymentDbConnectionString)
            .Options;
        return new PaymentDbContext(options);
    }

    public MatchingDbContext CreateMatchingDbContext()
    {
        if (string.IsNullOrEmpty(MatchingDbConnectionString))
            throw new InvalidOperationException("MatchingDbConnectionString is only available in persistent mode.");

        var options = new DbContextOptionsBuilder<MatchingDbContext>()
            .UseNpgsql(MatchingDbConnectionString)
            .Options;
        return new MatchingDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        GatewayClient.Dispose();
        await GatewayApp.StopAsync();
        await GatewayApp.DisposeAsync();

        await TripApp.StopAsync();
        await TripApp.DisposeAsync();

        await MatchingApp.StopAsync();
        await MatchingApp.DisposeAsync();

        await PaymentApp.StopAsync();
        await PaymentApp.DisposeAsync();
    }
}
