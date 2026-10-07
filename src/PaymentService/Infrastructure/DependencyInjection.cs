using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SwiftRide.PaymentService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("PaymentDB")
            ?? throw new InvalidOperationException("ConnectionStrings:PaymentDB is required.");
        services.AddDbContext<PaymentDbContext>(options => options.UseNpgsql(connectionString));
        return services;
    }
}
