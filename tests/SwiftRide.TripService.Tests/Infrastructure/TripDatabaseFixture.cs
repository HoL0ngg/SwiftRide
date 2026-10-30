using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Tests.Infrastructure;

public sealed class LocalSqlServerFactAttribute : FactAttribute
{
    public LocalSqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRIP_TEST_DB")))
        {
            Skip = "Set TRIP_TEST_DB to a disposable local SQL Server database.";
        }
    }
}

[CollectionDefinition("TripDatabase")]
public class TripDatabaseCollection : ICollectionFixture<TripDatabaseFixture>
{
}

public sealed class TripDatabaseFixture : IAsyncLifetime
{
    public string? ConnectionString { get; }

    public TripDatabaseFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("TRIP_TEST_DB");
        if (string.IsNullOrEmpty(ConnectionString))
        {
            return;
        }

        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var host = builder.DataSource.Split(',')[0].Trim();
        if (host is not ("localhost" or "127.0.0.1"))
        {
            throw new InvalidOperationException($"Test database must run on localhost. Detected host: {host}");
        }

        if (!builder.InitialCatalog.Equals("swiftride_trip_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Test database name must be 'swiftride_trip_test'. Detected: {builder.InitialCatalog}");
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            return;
        }

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM IntegrationInbox; DELETE FROM IntegrationOutbox; DELETE FROM TripPaymentJobs; DELETE FROM TripDriverRejections; DELETE FROM ActiveDriverAssignments; DELETE FROM ActiveRiderTrips; DELETE FROM CommandReceipts; DELETE FROM Trips;");
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
    }

    public TripDbContext CreateDbContext()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            throw new InvalidOperationException("TRIP_TEST_DB environment variable is not configured.");
        }

        var options = new DbContextOptionsBuilder<TripDbContext>()
            .UseSqlServer(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new TripDbContext(options);
    }
}
