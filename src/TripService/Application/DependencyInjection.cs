using Microsoft.Extensions.DependencyInjection;
using SwiftRide.TripService.Application.Payments;
using SwiftRide.TripService.Application.Trips;

namespace SwiftRide.TripService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddTripApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddOptions<TripLifecycleOptions>()
            .BindConfiguration("TripLifecycle")
            .Validate(o => o.MaxDriverRejections > 0, "MaxDriverRejections must be > 0")
            .Validate(o => o.SupportedPricingModes != null && o.SupportedPricingModes.Length > 0, "SupportedPricingModes must not be empty")
            .ValidateOnStart();

        services.AddOptions<TripRecoveryOptions>()
            .BindConfiguration("TripRecovery")
            .Validate(o => o.MatchingMaxAttempts > 0, "MatchingMaxAttempts must be > 0")
            .ValidateOnStart();

        services.AddOptions<PaymentWorkflowOptions>()
            .BindConfiguration("PaymentWorkflow")
            .Validate(o => o.MaxAttempts > 0, "MaxAttempts must be > 0")
            .Validate(o => o.TotalBudget > TimeSpan.Zero, "TotalBudget must be > 0")
            .ValidateOnStart();

        services.AddScoped<TripWorkCoordinator>();
        services.AddScoped<TripPaymentCoordinator>();
        services.AddScoped<CreateTripUseCase>();
        services.AddScoped<TripCommandUseCase>();
        services.AddScoped<TripQueryService>();
        return services;
    }
}
