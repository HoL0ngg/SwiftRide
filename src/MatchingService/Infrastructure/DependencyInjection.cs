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
        var isLocal = connection.Host is "localhost" or "127.0.0.1" or "host.docker.internal";
        if (!isLocal && connection.SslMode is not (SslMode.VerifyFull or SslMode.Require))
            throw new InvalidOperationException("MatchDB requires SSL Mode=VerifyFull or Require for non-local environments.");
        if (connection.Timeout <= 0 || connection.Timeout > 30)
            connection.Timeout = 15;
        if (connection.CommandTimeout <= 0 || connection.CommandTimeout > 60)
            connection.CommandTimeout = 30;
        services.AddDbContext<MatchingDbContext>(options => options.UseNpgsql(connection.ConnectionString));
        services.AddScoped<MatchingRepository>();
        services.AddScoped<IDriverLocationReader>(p => p.GetRequiredService<MatchingRepository>());
        services.AddScoped<IDriverLocationWriter>(p => p.GetRequiredService<MatchingRepository>());
        services.AddScoped<IQuoteWriter>(p => p.GetRequiredService<MatchingRepository>());
        services.AddGeocoding(configuration);
        return services;
    }
}
