using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.Contracts.V1;
using SwiftRide.MatchingService.Infrastructure.Consumers;

namespace SwiftRide.MatchingService.Infrastructure.Messaging;

public static class MessagingRegistration
{
    /// <summary>
    /// Registers the Matching consumers for Trip's quote/match requests. Transport resolution mirrors Trip and Payment:
    /// "RabbitMQ", "InMemory" (tests) or "None"/"Disabled" (REST-only mode, no consumers).
    /// </summary>
    public static IServiceCollection AddMatchingMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var rabbitHost = configuration["RabbitMQ:Host"];
        var rabbitUser = configuration["RabbitMQ:Username"] ?? configuration["RABBITMQ_DEFAULT_USER"] ?? "guest";
        var rabbitPass = configuration["RabbitMQ:Password"] ?? configuration["RABBITMQ_DEFAULT_PASS"] ?? "guest";
        var rabbitPort = ushort.TryParse(configuration["RabbitMQ:Port"], out var p) ? p : (ushort)5672;

        var transport = configuration["Messaging:Transport"];
        var resolvedTransport = !string.IsNullOrWhiteSpace(transport)
            ? transport.Trim()
            : (!string.IsNullOrWhiteSpace(rabbitHost) ? "RabbitMQ" : "None");

        if (string.Equals(resolvedTransport, "None", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(resolvedTransport, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        var isExplicitInMemory = string.Equals(resolvedTransport, "InMemory", StringComparison.OrdinalIgnoreCase);
        if (!isExplicitInMemory && !string.Equals(resolvedTransport, "RabbitMQ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported Messaging:Transport '{transport}'. Supported values: 'RabbitMQ', 'InMemory', 'None'.");
        }

        services.AddMassTransit(x =>
        {
            x.AddConsumer<QuoteRequestedConsumer>();
            x.AddConsumer<DriverMatchRequestedConsumer>();

            if (isExplicitInMemory)
            {
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseMessageRetry(r => BoundedMessageRetryPolicy.ConfigureRetry(r, configuration));
                    cfg.ConfigureEndpoints(context);
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(rabbitHost))
            {
                throw new InvalidOperationException("Messaging is configured for RabbitMQ transport, but RabbitMQ:Host is missing or empty. Set Messaging:Transport='InMemory' for explicit in-memory testing, or Messaging:Transport='None' for REST-only mode.");
            }

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(rabbitHost, rabbitPort, "/", h =>
                {
                    h.Username(rabbitUser);
                    h.Password(rabbitPass);
                });

                cfg.ReceiveEndpoint(EventTopology.Queues.MatchingQuoteRequested, e =>
                {
                    BoundedMessageRetryPolicy.ConfigureEndpoint(e, configuration);
                    e.ConfigureConsumer<QuoteRequestedConsumer>(context);
                });

                cfg.ReceiveEndpoint(EventTopology.Queues.MatchingDriverMatchRequested, e =>
                {
                    BoundedMessageRetryPolicy.ConfigureEndpoint(e, configuration);
                    e.ConfigureConsumer<DriverMatchRequestedConsumer>(context);
                });

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
