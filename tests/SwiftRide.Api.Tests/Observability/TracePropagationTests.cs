using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftRide.ApiGateway;
using SwiftRide.PaymentService.Api.Observability;
using SwiftRide.TripService.Api.Observability;
using SwiftRide.TripService.Infrastructure.Http;
using Xunit;

namespace SwiftRide.Api.Tests.Observability;

public class TracePropagationTests
{
    private async Task<(WebApplication App, HttpClient Client)> CreateGatewayClientAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var configValues = new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "DevelopmentJwt",
            ["Jwt:Issuer"] = "SwiftRide",
            ["Jwt:Audience"] = "SwiftRideClients",
            ["Jwt:SigningKey"] = "SuperSecretKeyForDevelopmentTestingOnlyMustBe32BytesLong!",
            ["RateLimit:PermitLimit"] = "1000",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "100",
            ["ReverseProxy:Clusters:trip-service:Destinations:primary:Address"] = "http://127.0.0.1:5081/",
            ["ReverseProxy:Clusters:matching-service:Destinations:primary:Address"] = "http://127.0.0.1:5082/",
            ["ReverseProxy:Routes:trips:ClusterId"] = "trip-service",
            ["ReverseProxy:Routes:trips:Match:Path"] = "/trips/{**catch-all}",
            ["ReverseProxy:Routes:trips:AuthorizationPolicy"] = "public",
        };

        builder.Configuration.AddInMemoryCollection(configValues);
        var app = GatewayApplication.Build(builder);
        await app.StartAsync();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return (app, client);
    }

    [Fact]
    public async Task Gateway_Echoes_Given_Valid_CorrelationId()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add("X-Correlation-Id", "client-trace-12345");

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.True(response.Headers.Contains("X-Correlation-Id"));
            var returnedCorrId = response.Headers.GetValues("X-Correlation-Id").First();
            Assert.Equal("client-trace-12345", returnedCorrId);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Gateway_Generates_Guid_When_CorrelationId_Missing()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.True(response.Headers.Contains("X-Correlation-Id"));
            var returnedCorrId = response.Headers.GetValues("X-Correlation-Id").First();
            Assert.True(Guid.TryParse(returnedCorrId, out _), $"Expected valid GUID, but was '{returnedCorrId}'.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Gateway_Sanitizes_Excessive_Length_CorrelationId()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            var overlyLongId = new string('a', 200);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add("X-Correlation-Id", overlyLongId);

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var returnedCorrId = response.Headers.GetValues("X-Correlation-Id").First();
            Assert.Equal(128, returnedCorrId.Length);
            Assert.Equal(new string('a', 128), returnedCorrId);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Gateway_Replaces_Invalid_Characters_In_CorrelationId()
    {
        var (app, client) = await CreateGatewayClientAsync();
        try
        {
            var injectionPayload = "<script>alert('xss')</script>";
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add("X-Correlation-Id", injectionPayload);

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var returnedCorrId = response.Headers.GetValues("X-Correlation-Id").First();
            Assert.DoesNotContain("<script>", returnedCorrId);
            Assert.True(Guid.TryParse(returnedCorrId, out _));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task CorrelationPropagationHandler_Injects_CorrelationId_From_HttpContext()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        httpContextAccessor.HttpContext.Items["CorrelationId"] = "trace-from-request-context-123";

        var testHandler = new TestRecordingHandler();
        var propagationHandler = new CorrelationPropagationHandler(httpContextAccessor)
        {
            InnerHandler = testHandler
        };

        using var invoker = new HttpMessageInvoker(propagationHandler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://matching-service/match");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.NotNull(testHandler.LastRequest);
        Assert.True(testHandler.LastRequest.Headers.Contains("X-Correlation-Id"));
        Assert.Equal("trace-from-request-context-123", testHandler.LastRequest.Headers.GetValues("X-Correlation-Id").First());
    }

    [Fact]
    public async Task CorrelationPropagationHandler_Injects_CorrelationId_From_Activity_When_Context_Missing()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = null
        };

        using var activitySource = new ActivitySource("TestActivitySource");
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(activityListener);

        using var activity = activitySource.StartActivity("TestAction");
        activity?.SetTag("correlation.id", "trace-from-activity-tag-456");

        var testHandler = new TestRecordingHandler();
        var propagationHandler = new CorrelationPropagationHandler(httpContextAccessor)
        {
            InnerHandler = testHandler
        };

        using var invoker = new HttpMessageInvoker(propagationHandler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://payment-service/payments");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.NotNull(testHandler.LastRequest);
        Assert.True(testHandler.LastRequest.Headers.Contains("X-Correlation-Id"));
        Assert.Equal("trace-from-activity-tag-456", testHandler.LastRequest.Headers.GetValues("X-Correlation-Id").First());
    }

    [Fact]
    public void TripService_ConsumerActivity_Created_When_ObservabilityRegistered()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddTripObservability(config);
        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetService<OpenTelemetry.Trace.TracerProvider>();
        Assert.NotNull(tracerProvider);

        // Simulate incoming W3C traceparent
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var parentContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);

        var consumerSource = new ActivitySource(SwiftRide.TripService.Api.Observability.TripTelemetry.ConsumerActivitySourceName, "1.0.0");
        using var activity = consumerSource.StartActivity("Consume PaymentSettled", ActivityKind.Consumer, parentContext);

        Assert.NotNull(activity);
        Assert.Equal(traceId, activity.TraceId);
        Assert.Equal(spanId, activity.ParentSpanId);
    }

    [Fact]
    public void PaymentService_ConsumerActivity_Created_When_ObservabilityRegistered()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddPaymentObservability(config);
        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetService<OpenTelemetry.Trace.TracerProvider>();
        Assert.NotNull(tracerProvider);

        // Simulate incoming W3C traceparent
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var parentContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);

        var consumerSource = new ActivitySource(SwiftRide.PaymentService.Api.Observability.PaymentTelemetry.ConsumerActivitySourceName, "1.0.0");
        using var activity = consumerSource.StartActivity("Consume RideCompleted", ActivityKind.Consumer, parentContext);

        Assert.NotNull(activity);
        Assert.Equal(traceId, activity.TraceId);
        Assert.Equal(spanId, activity.ParentSpanId);
    }

    private sealed class TestRecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
