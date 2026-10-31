using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;
using SwiftRide.MatchingService.Infrastructure.Persistence;

namespace SwiftRide.MatchingService.Tests;

public sealed class LocalPostgresFactAttribute : FactAttribute
{
    public LocalPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MATCHING_TEST_DB")))
            Skip = "Set MATCHING_TEST_DB to a disposable local PostgreSQL database.";
    }
}

public class PostgresRepositoryTests
{
    [LocalPostgresFact]
    public async Task Existing_schema_mapping_upsert_and_quote_snapshot_work_on_postgres()
    {
        var connection = Environment.GetEnvironmentVariable("MATCHING_TEST_DB")!;
        var cs = new NpgsqlConnectionStringBuilder(connection);
        Assert.Contains(cs.Host, new[] { "localhost", "127.0.0.1" });
        Assert.Equal("matching_test", cs.Database);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SwiftRide.sln"))) root = root.Parent;
        var sql = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "database/supabase/matchdb-paydb.sql"));
        var start = sql.IndexOf("create table matchdb.driver_locations", StringComparison.Ordinal);
        var end = sql.IndexOf("create table paydb.payments", StringComparison.Ordinal);

        await using (var conn = new NpgsqlConnection(connection))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS matchdb;";
            await cmd.ExecuteNonQueryAsync();

            try
            {
                cmd.CommandText = sql[start..end];
                await cmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P07") { }
        }

        await using var db = new MatchingDbContext(new DbContextOptionsBuilder<MatchingDbContext>().UseNpgsql(connection).Options);
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.DriverLocations.ExecuteDeleteAsync();
        await db.Quotes.ExecuteDeleteAsync();

        var repository = new MatchingRepository(db);
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertAsync(new("driver-1", new(10, 106), true, now), default);
        await repository.UpsertAsync(new("driver-1", new(11, 107), true, now.AddSeconds(1)), default);
        await repository.UpsertAsync(new("driver-1", new(9, 105), false, now.AddSeconds(-1)), default);
        var driver = Assert.Single(await repository.ReadAvailableAsync(now.AddMinutes(-5), default));
        Assert.Equal(11, driver.Position.Latitude);
        Assert.Equal(107, driver.Position.Longitude);
        var snapshot = new QuoteSnapshot(Guid.NewGuid(), new PricingContext(5, 10, 10000, 1000, 1.2m, 5000, .9m, .08m), 74844, "VND", now);
        await repository.AddAsync(snapshot, default);
        db.ChangeTracker.Clear();
        var quote = await db.Quotes.SingleAsync();
        Assert.Equal(snapshot.Id, quote.Id);
        Assert.Equal(74844, quote.Amount);
        Assert.Equal(.9m, quote.PromotionFactor);
        Assert.Equal(1.2m, quote.Surge);
        Assert.Equal("matchdb", db.Model.FindEntityType(typeof(QuoteRow))!.GetSchema());
        await transaction.RollbackAsync();
    }
}
