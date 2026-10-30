using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Repositories;
using SwiftRide.PaymentService.Infrastructure.Gateways;
using SwiftRide.PaymentService.Infrastructure.Messaging;
using SwiftRide.PaymentService.Infrastructure.Persistence;

namespace SwiftRide.PaymentService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("PaymentDB")
            ?? throw new InvalidOperationException("ConnectionStrings:PaymentDB is required.");

        services.AddDbContext<PaymentDbContext>(options => options.UseNpgsql(connectionString));
        services.AddDbContextFactory<PaymentDbContext>(options => options.UseNpgsql(connectionString), ServiceLifetime.Scoped);

        services.AddScoped<IPaymentReader, PaymentRepository>();
        services.AddScoped<IPaymentWriter, PaymentRepository>();
        services.AddScoped<IUnitOfWork, PaymentUnitOfWork>();
        services.AddScoped<IPaymentProcessingStore, PaymentProcessingStore>();
        services.AddScoped<IRefundProcessingStore, RefundProcessingStore>();
        services.AddScoped<IMockOperationStore, MockOperationStore>();

        services.AddScoped<IPaymentGateway, MockCardGateway>();
        services.AddScoped<IPaymentGateway, MockWalletGateway>();
        services.AddScoped<IPaymentGatewayFactory, PaymentGatewayFactory>();

        var seconds = int.TryParse(configuration["PaymentGateway:TimeoutSeconds"], out var s) && s > 0 ? s : 10;
        services.AddSingleton(new PaymentGatewaySettings(TimeSpan.FromSeconds(seconds)));

        services.AddPaymentMessaging(configuration);

        return services;
    }
}
