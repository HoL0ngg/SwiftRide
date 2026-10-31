using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Infrastructure.Http;
using SwiftRide.TripService.Infrastructure.Persistence;
using SwiftRide.TripService.Infrastructure.Workers;
using SwiftRide.TripService.Infrastructure.Messaging;

namespace SwiftRide.TripService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("TripDB");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<TripDbContext>(options => options.UseSqlServer(connectionString).ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        }

        services.AddScoped<TripRepository>();
        services.AddScoped<ITripReader>(sp => sp.GetRequiredService<TripRepository>());
        services.AddScoped<ITripWriter>(sp => sp.GetRequiredService<TripRepository>());
        services.AddScoped<ITripUnitOfWork, TripUnitOfWork>();
        services.AddScoped<IActiveAssignmentStore, ActiveAssignmentStore>();
        services.AddScoped<ICommandReceiptStore, CommandReceiptStore>();
        services.AddScoped<ITripWorkStore, TripWorkStore>();
        services.AddScoped<ITripQueryReader, TripQueryReader>();

        services.AddHostedService<PendingTripWorker>();
        services.AddHostedService<PendingPaymentWorker>();
        services.AddTripMessaging(configuration);

        var matchingOptions = configuration.GetSection(MatchingClientOptions.SectionName).Get<MatchingClientOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(matchingOptions.BaseUrl))
        {
            matchingOptions.BaseUrl = configuration["Services:Matching:BaseAddress"] ?? "";
        }
        if (string.IsNullOrWhiteSpace(matchingOptions.InternalApiKey))
        {
            matchingOptions.InternalApiKey = configuration["Services:Matching:ApiKey"] ?? "";
        }

        var paymentOptions = configuration.GetSection(PaymentClientOptions.SectionName).Get<PaymentClientOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(paymentOptions.BaseUrl))
        {
            paymentOptions.BaseUrl = configuration["Services:Payment:BaseAddress"] ?? "";
        }
        if (string.IsNullOrWhiteSpace(paymentOptions.InternalApiKey))
        {
            paymentOptions.InternalApiKey = configuration["Services:Payment:ApiKey"] ?? "";
        }

        services.AddHttpContextAccessor();
        services.AddTransient<CorrelationPropagationHandler>();

        services.AddHttpClient<IMatchingClient, MatchingClient>(client =>
        {
            if (!string.IsNullOrWhiteSpace(matchingOptions.BaseUrl))
            {
                client.BaseAddress = new Uri(matchingOptions.BaseUrl);
            }
        })
        .AddHttpMessageHandler<CorrelationPropagationHandler>()
        .AddHttpMessageHandler(() => new InternalKeyHandler(matchingOptions.InternalApiKey));

        services.AddHttpClient<IPaymentClient, PaymentClient>(client =>
        {
            if (!string.IsNullOrWhiteSpace(paymentOptions.BaseUrl))
            {
                client.BaseAddress = new Uri(paymentOptions.BaseUrl);
            }
        })
        .AddHttpMessageHandler<CorrelationPropagationHandler>()
        .AddHttpMessageHandler(() => new InternalKeyHandler(paymentOptions.InternalApiKey));

        return services;
    }
}
