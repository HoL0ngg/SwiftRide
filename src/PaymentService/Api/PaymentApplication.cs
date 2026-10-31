using System.Text.Json.Serialization;
using SwiftRide.PaymentService.Application;
using SwiftRide.PaymentService.Infrastructure;
using SwiftRide.PaymentService.Api.Observability;

namespace SwiftRide.PaymentService.Api;

public static class PaymentApplication
{
    public static WebApplication Build(
        WebApplicationBuilder builder,
        Action<IServiceCollection>? configureServices = null)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddPaymentObservability(builder.Configuration);
        builder.Services.AddSingleton<SwiftRide.PaymentService.Application.Abstractions.IPaymentMetrics, SwiftRide.PaymentService.Api.Observability.PaymentMetrics>();
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        builder.Services.AddPaymentApplication();
        builder.Services.AddInfrastructure(builder.Configuration);

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.MapGet("/health", () => Results.Ok());
        app.MapPaymentEndpoints();

        return app;
    }
}
