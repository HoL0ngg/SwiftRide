using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace SwiftRide.TripService.Api.Observability;

public static class TripTelemetry
{
    public const string ServiceName = "SwiftRide.TripService";
    public const string ServiceVersion = "1.0.0";
    public const string ConsumerActivitySourceName = "SwiftRide.TripService.Consumer";
    public const string OutboxActivitySourceName = "SwiftRide.TripService.Outbox";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static readonly Counter<long> TripsCreated = Meter.CreateCounter<long>(
        "swiftride.trips.created",
        "trips",
        "Số lượng chuyến đi được tạo thành công.");

    public static readonly Counter<long> TripsCompleted = Meter.CreateCounter<long>(
        "swiftride.trips.completed",
        "trips",
        "Số lượng chuyến đi hoàn tất.");

    public static readonly Counter<long> TripsFailed = Meter.CreateCounter<long>(
        "swiftride.trips.failed",
        "trips",
        "Số lượng chuyến đi thất bại.");

    public static IServiceCollection AddTripObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ServiceName)
                    .AddSource(ConsumerActivitySourceName)
                    .AddSource(OutboxActivitySourceName)
                    .AddSource("SwiftRide.TripService*")
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = true;
                    })
                    .AddHttpClientInstrumentation();

                var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
                if (!string.IsNullOrWhiteSpace(otlpEndpoint) && Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var uri))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = uri);
                }

                if (configuration.GetValue<bool>("OTEL_CONSOLE_EXPORTER_ENABLED"))
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(ServiceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
                if (!string.IsNullOrWhiteSpace(otlpEndpoint) && Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var uri))
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = uri);
                }

                if (configuration.GetValue<bool>("OTEL_CONSOLE_EXPORTER_ENABLED"))
                {
                    metrics.AddConsoleExporter();
                }
            });

        return services;
    }
}
