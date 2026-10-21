using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Repositories;
using SwiftRide.PaymentService.Infrastructure.Gateways;
using SwiftRide.PaymentService.Infrastructure.Persistence;

namespace SwiftRide.PaymentService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("PaymentDB")
            ?? throw new InvalidOperationException("ConnectionStrings:PaymentDB is required.");
        services.AddDbContext<PaymentDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IPaymentReader, PaymentRepository>();         // 28.1 Port (Domain) -> Adapter (Infrastructure)
        services.AddScoped<IPaymentWriter, PaymentRepository>();
        services.AddScoped<IUnitOfWork, PaymentUnitOfWork>();

        services.AddSingleton<IPaymentGateway, MockCardGateway>();       // 28.2 Muốn thêm ví/ngân hàng: thêm 1 dòng ở đây
        services.AddSingleton<IPaymentGateway, MockWalletGateway>();
        services.AddSingleton<IPaymentGatewayFactory, PaymentGatewayFactory>();

        var seconds = int.TryParse(configuration["PaymentGateway:TimeoutSeconds"], out var s) && s > 0 ? s : 10;
        services.AddSingleton(new PaymentGatewaySettings(TimeSpan.FromSeconds(seconds)));
        return services;
    }
}