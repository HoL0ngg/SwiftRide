using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SwiftRide.TripService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("TripDB")
            ?? throw new InvalidOperationException("ConnectionStrings:TripDB is required.");
        services.AddDbContext<TripDbContext>(options => options.UseSqlServer(connectionString));
        return services;
    }
}
