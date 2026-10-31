using MassTransit;
using Microsoft.Extensions.Configuration;
using SwiftRide.PaymentService.Domain.Exceptions;

namespace SwiftRide.PaymentService.Infrastructure.Messaging;

public static class BoundedMessageRetryPolicy
{
    public const int DefaultRetryCount = 3;
    public const int DefaultMinIntervalSeconds = 30;
    public const int DefaultMaxIntervalSeconds = 120;
    public const int DefaultIntervalDeltaSeconds = 30;
    public const int DefaultConcurrentMessageLimit = 2;
    public const int DefaultPrefetchCount = 4;

    public static void ConfigureRetry(IRetryConfigurator r, IConfiguration configuration)
    {
        var fastMode = string.Equals(configuration["RabbitMQ:FastRetryForTests"], "true", StringComparison.OrdinalIgnoreCase);
        if (fastMode)
        {
            r.Intervals(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(150));
        }
        else
        {
            var retryCount = int.TryParse(configuration["RabbitMQ:RetryCount"], out var rc) ? rc : DefaultRetryCount;
            var minSec = int.TryParse(configuration["RabbitMQ:RetryMinIntervalSeconds"], out var min) ? min : DefaultMinIntervalSeconds;
            var maxSec = int.TryParse(configuration["RabbitMQ:RetryMaxIntervalSeconds"], out var max) ? max : DefaultMaxIntervalSeconds;
            var deltaSec = int.TryParse(configuration["RabbitMQ:RetryDeltaSeconds"], out var delta) ? delta : DefaultIntervalDeltaSeconds;

            r.Exponential(
                retryLimit: retryCount,
                minInterval: TimeSpan.FromSeconds(minSec),
                maxInterval: TimeSpan.FromSeconds(maxSec),
                intervalDelta: TimeSpan.FromSeconds(deltaSec));
        }

        // Domain rules and permanent validation errors should not be retried:
        r.Ignore<DomainException>();
        r.Ignore<InvalidPaymentStateException>();
        r.Ignore<ArgumentException>();
        r.Ignore<FormatException>();
        r.Ignore<System.Text.Json.JsonException>();
    }

    public static void ConfigureEndpoint(IReceiveEndpointConfigurator endpoint, IConfiguration configuration)
    {
        var concurrentLimit = int.TryParse(configuration["RabbitMQ:ConcurrentMessageLimit"], out var cl) ? cl : DefaultConcurrentMessageLimit;
        var prefetch = int.TryParse(configuration["RabbitMQ:PrefetchCount"], out var pf) ? pf : DefaultPrefetchCount;

        endpoint.ConcurrentMessageLimit = concurrentLimit;
        endpoint.PrefetchCount = prefetch;
        endpoint.UseMessageRetry(r => ConfigureRetry(r, configuration));
    }
}
