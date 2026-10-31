using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftRide.TripService.Application;
using SwiftRide.TripService.Application.Payments;
using SwiftRide.TripService.Application.Trips;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

public class TripOptionsValidationTests
{
    private static IServiceCollection CreateServices(IConfiguration? config = null)
    {
        var services = new ServiceCollection();
        config ??= new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddTripApplication();
        return services;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TripLifecycleOptions_InvalidMaxDriverRejections_FailsValidation(int maxRejections)
    {
        var services = CreateServices();
        services.Configure<TripLifecycleOptions>(o => o.MaxDriverRejections = maxRejections);

        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TripLifecycleOptions>>().Value);
        Assert.Contains("MaxDriverRejections must be > 0", ex.Message);
    }

    [Fact]
    public void TripLifecycleOptions_EmptySupportedPricingModes_FailsValidation()
    {
        var services = CreateServices();
        services.Configure<TripLifecycleOptions>(o => o.SupportedPricingModes = Array.Empty<string>());

        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TripLifecycleOptions>>().Value);
        Assert.Contains("SupportedPricingModes must not be empty", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TripRecoveryOptions_InvalidMatchingMaxAttempts_FailsValidation(int maxAttempts)
    {
        var services = CreateServices();
        services.Configure<TripRecoveryOptions>(o => o.MatchingMaxAttempts = maxAttempts);

        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TripRecoveryOptions>>().Value);
        Assert.Contains("MatchingMaxAttempts must be > 0", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void PaymentWorkflowOptions_InvalidMaxAttempts_FailsValidation(int maxAttempts)
    {
        var services = CreateServices();
        services.Configure<PaymentWorkflowOptions>(o => o.MaxAttempts = maxAttempts);

        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PaymentWorkflowOptions>>().Value);
        Assert.Contains("MaxAttempts must be > 0", ex.Message);
    }

    [Fact]
    public void PaymentWorkflowOptions_NonPositiveTotalBudget_FailsValidation()
    {
        var services = CreateServices();
        services.Configure<PaymentWorkflowOptions>(o => o.TotalBudget = TimeSpan.Zero);

        var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PaymentWorkflowOptions>>().Value);
        Assert.Contains("TotalBudget must be > 0", ex.Message);
    }
}
