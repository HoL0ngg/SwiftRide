using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Infrastructure.Geocoding;
using SwiftRide.MatchingService.Infrastructure.Persistence;

namespace SwiftRide.MatchingService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MatchDB")
            ?? throw new InvalidOperationException("ConnectionStrings:MatchDB is required.");
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.SslMode != SslMode.VerifyFull || connection.Timeout is <= 0 or > 30 || connection.CommandTimeout is <= 0 or > 60)
            throw new InvalidOperationException("MatchDB requires SSL Mode=VerifyFull and bounded connection/command timeouts.");
        services.AddDbContext<MatchingDbContext>(options => options.UseNpgsql(connection.ConnectionString));
        services.AddScoped<MatchingRepository>();
        services.AddScoped<IDriverLocationReader>(p => p.GetRequiredService<MatchingRepository>());
        services.AddScoped<IDriverLocationWriter>(p => p.GetRequiredService<MatchingRepository>());
        services.AddScoped<IQuoteWriter>(p => p.GetRequiredService<MatchingRepository>());
        services.AddGeocoding(configuration);
        return services;
    }
}
