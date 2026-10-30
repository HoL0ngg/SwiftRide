using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Messaging;
using SwiftRide.TripService.Infrastructure.Consumers;

namespace SwiftRide.TripService.Infrastructure.Messaging;

public static class MessagingRegistration
{
    public static IServiceCollection AddTripMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var rabbitHost = configuration["RabbitMQ:Host"];
        var rabbitUser = configuration["RabbitMQ:Username"] ?? configuration["RABBITMQ_DEFAULT_USER"] ?? "guest";
        var rabbitPass = configuration["RabbitMQ:Password"] ?? configuration["RABBITMQ_DEFAULT_PASS"] ?? "guest";
        var rabbitPort = ushort.TryParse(configuration["RabbitMQ:Port"], out var p) ? p : (ushort)5672;

        var transport = configuration["Messaging:Transport"];

        // Explicit transport mode resolution:
        // 1. "InMemory": Explicit in-memory transport for lightweight testing.
        // 2. "RabbitMQ": Explicit RabbitMQ transport, requires valid RabbitMQ:Host (fails fast if missing).
        // 3. "None" / "Disabled": Messaging broker is disabled (e.g. running default REST MVP).
        // 4. Unspecified (empty/null): If RabbitMQ:Host is configured, defaults to RabbitMQ; otherwise defaults to None (safe REST-only default).
        var resolvedTransport = !string.IsNullOrWhiteSpace(transport)
            ? transport.Trim()
            : (!string.IsNullOrWhiteSpace(rabbitHost) ? "RabbitMQ" : "None");

        if (string.Equals(resolvedTransport, "None", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(resolvedTransport, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<ITripEventWriter, NoOpTripEventWriter>();
            return services;
        }

        var isExplicitInMemory = string.Equals(resolvedTransport, "InMemory", StringComparison.OrdinalIgnoreCase);
        if (!isExplicitInMemory && !string.Equals(resolvedTransport, "RabbitMQ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported Messaging:Transport '{transport}'. Supported values: 'RabbitMQ', 'InMemory', 'None'.");
        }

        services.AddScoped<ITripEventWriter, TripEventWriter>();
        services.AddHostedService<TripOutboxPublisher>();

        services.AddMassTransit(x =>
        {
            x.AddConsumer<PaymentSettledConsumer>();
            x.AddConsumer<PaymentFailedConsumer>();
            x.AddConsumer<PaymentRefundedConsumer>();
            x.AddConsumer<PaymentCompensationFailedConsumer>();

            if (isExplicitInMemory)
            {
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseMessageRetry(r => BoundedMessageRetryPolicy.ConfigureRetry(r, configuration));
                    cfg.ConfigureEndpoints(context);
                });
            }
            else
            {
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

                    cfg.ReceiveEndpoint(EventTopology.Queues.TripPaymentSettled, e =>
                    {
                        BoundedMessageRetryPolicy.ConfigureEndpoint(e, configuration);
                        e.ConfigureConsumer<PaymentSettledConsumer>(context);
                    });

                    cfg.ReceiveEndpoint(EventTopology.Queues.TripPaymentFailed, e =>
                    {
                        BoundedMessageRetryPolicy.ConfigureEndpoint(e, configuration);
                        e.ConfigureConsumer<PaymentFailedConsumer>(context);
                    });

                    cfg.ReceiveEndpoint(EventTopology.Queues.TripPaymentRefunded, e =>
                    {
                        BoundedMessageRetryPolicy.ConfigureEndpoint(e, configuration);
                        e.ConfigureConsumer<PaymentRefundedConsumer>(context);
                    });

                    cfg.ReceiveEndpoint(EventTopology.Queues.TripPaymentCompensationFailed, e =>
                    {
                        BoundedMessageRetryPolicy.ConfigureEndpoint(e, configuration);
                        e.ConfigureConsumer<PaymentCompensationFailedConsumer>(context);
                    });

                    cfg.ConfigureEndpoints(context);
                });
            }
        });

        return services;
    }
}
