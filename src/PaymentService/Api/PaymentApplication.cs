using System.Text.Json.Serialization;
using SwiftRide.PaymentService.Application;
using SwiftRide.PaymentService.Infrastructure;
using SwiftRide.PaymentService.Api.Observability;
using Microsoft.Extensions.Options;
using SwiftRide.PaymentService.Application.Payments;
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
        builder.Services.AddOptions<PaymentProcessingSettings>()
            .Bind(builder.Configuration.GetSection(PaymentProcessingSettings.SectionName))
            .Validate(s => s.MaxAttempts >= 1, "PaymentProcessing:MaxAttempts must be >= 1")
            .Validate(s => s.LeaseSeconds >= 1, "PaymentProcessing:LeaseSeconds must be >= 1")
            .Validate(s => s.PendingLeaseSeconds >= 1, "PaymentProcessing:PendingLeaseSeconds must be >= 1")
            .ValidateOnStart();

        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<PaymentProcessingSettings>>().Value);
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
