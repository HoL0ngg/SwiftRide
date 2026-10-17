using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SwiftRide.Api.Tests;

public class GlobalExceptionHandlerTests
{
    public static TheoryData<Type, string> Handlers => new()
    {
        { typeof(ApiGateway.GlobalExceptionHandler), "Production" },
        { typeof(TripService.Api.GlobalExceptionHandler), "Production" },
        { typeof(MatchingService.Api.GlobalExceptionHandler), "Production" },
        { typeof(PaymentService.Api.GlobalExceptionHandler), "Production" },
        { typeof(ApiGateway.GlobalExceptionHandler), "Development" },
        { typeof(TripService.Api.GlobalExceptionHandler), "Development" },
        { typeof(MatchingService.Api.GlobalExceptionHandler), "Development" },
        { typeof(PaymentService.Api.GlobalExceptionHandler), "Development" }
    };

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Pipeline_ReturnsSafeProblemDetails(Type handlerType, string environment)
    {
        const string sensitiveMessage = "token=private-payment-token;Password=private-database-password";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler(_ => { });
        builder.Services.AddSingleton(typeof(IExceptionHandler), handlerType);

        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapGet("/unexpected", (Func<IResult>)(() => throw new Exception(sensitiveMessage)));
        app.MapGet("/argument", (Func<IResult>)(() => throw new ArgumentException(sensitiveMessage)));
        app.MapGet("/bad-request", (Func<IResult>)(() => throw new BadHttpRequestException(sensitiveMessage)));
        app.MapGet("/too-large", (Func<IResult>)(() => throw new BadHttpRequestException(sensitiveMessage, 413)));
        await app.StartAsync();

        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single()),
            Timeout = TimeSpan.FromSeconds(10)
        };
        // Error responses must stay JSON even if the caller prefers HTML.
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        foreach (var (path, status) in new[]
        {
            ("/unexpected", 500), ("/argument", 500), ("/bad-request", 400), ("/too-large", 413)
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(sensitiveMessage, body);
            Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.NotNull(problem);
            Assert.Equal(status, problem.Status);
            Assert.False(string.IsNullOrWhiteSpace(problem.Title));
            Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
            Assert.False(string.IsNullOrWhiteSpace(((JsonElement)problem.Extensions["traceId"]!).GetString()));
        }

        await app.StopAsync();
    }
}
