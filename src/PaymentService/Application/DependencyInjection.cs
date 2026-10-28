using Microsoft.Extensions.DependencyInjection;
using SwiftRide.PaymentService.Application.Payments;
using SwiftRide.PaymentService.Application.Payments.UseCases;
namespace SwiftRide.PaymentService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddPaymentApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);   // 19.1 Inject đồng hồ để test được
        services.AddScoped<GatewayCallPolicy>();
        services.AddScoped<ProcessPaymentUseCase>();  // 19.2 Use case là Java-thuần-kiểu: không attribute, đăng ký ở đây
        services.AddScoped<RefundPaymentUseCase>();
        services.AddScoped<GetPaymentUseCase>();
        services.AddScoped<GetPaymentByTripUseCase>();
        return services;
    }
}