using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace SwiftRide.PaymentService.Api.Observability;

public static class PaymentTelemetry
{
    public const string ServiceName = "SwiftRide.PaymentService";
    public const string ServiceVersion = "1.0.0";
    public const string ConsumerActivitySourceName = "SwiftRide.PaymentService.Consumer";
    public const string OutboxActivitySourceName = "SwiftRide.PaymentService.Outbox";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static readonly Counter<long> PaymentsSettled = Meter.CreateCounter<long>(
        "swiftride.payments.settled",
        "payments",
        "Số lượng thanh toán đã hoàn tất thành công.");

    public static readonly Counter<long> PaymentsFailed = Meter.CreateCounter<long>(
        "swiftride.payments.failed",
        "payments",
        "Số lượng thanh toán thất bại.");

    public static readonly Counter<long> PaymentsRefunded = Meter.CreateCounter<long>(
        "swiftride.payments.refunded",
        "payments",
        "Số lượng giao dịch hoàn tiền bồi hoàn.");

    public static IServiceCollection AddPaymentObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ServiceName)
                    .AddSource(ConsumerActivitySourceName)
                    .AddSource(OutboxActivitySourceName)
                    .AddSource("SwiftRide.PaymentService*")
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
