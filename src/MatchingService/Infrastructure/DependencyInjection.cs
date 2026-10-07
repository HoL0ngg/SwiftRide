using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace SwiftRide.MatchingService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MatchDB")
            ?? throw new InvalidOperationException("ConnectionStrings:MatchDB is required.");
        var databaseName = configuration["MongoDB:Database"]
            ?? throw new InvalidOperationException("MongoDB:Database is required.");
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton<IMongoDatabase>(provider =>
            provider.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
        return services;
    }
}
