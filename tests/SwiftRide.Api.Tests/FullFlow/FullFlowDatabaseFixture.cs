using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftRide.MatchingService.Infrastructure.Persistence;
using SwiftRide.PaymentService.Infrastructure.Persistence;
using SwiftRide.TripService.Infrastructure.Persistence;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SwiftRide.Api.Tests.FullFlow;

public sealed class LocalPersistentFullFlowFactAttribute : FactAttribute
{
    public LocalPersistentFullFlowFactAttribute()
    {
        var tripDb = Environment.GetEnvironmentVariable("TRIP_TEST_DB");
        var payDb = Environment.GetEnvironmentVariable("PAYMENT_TEST_DB");
        var matchDb = Environment.GetEnvironmentVariable("MATCHING_TEST_DB");
        if (string.IsNullOrEmpty(tripDb) || string.IsNullOrEmpty(payDb) || string.IsNullOrEmpty(matchDb))
        {
            Skip = "Set TRIP_TEST_DB, PAYMENT_TEST_DB, and MATCHING_TEST_DB to run persistent FullFlow integration tests.";
        }
    }
}

[CollectionDefinition("PersistentFullFlow")]
public class PersistentFullFlowCollection : ICollectionFixture<FullFlowDatabaseFixture>
{
}

public sealed class FullFlowDatabaseFixture : IAsyncLifetime
{
    public string? TripDbConnectionString { get; }
    public string? PaymentDbConnectionString { get; }
    public string? MatchingDbConnectionString { get; }

    public FullFlowDatabaseFixture()
    {
        TripDbConnectionString = Environment.GetEnvironmentVariable("TRIP_TEST_DB");
        PaymentDbConnectionString = Environment.GetEnvironmentVariable("PAYMENT_TEST_DB");
        MatchingDbConnectionString = Environment.GetEnvironmentVariable("MATCHING_TEST_DB");

        if (string.IsNullOrEmpty(TripDbConnectionString) ||
            string.IsNullOrEmpty(PaymentDbConnectionString) ||
            string.IsNullOrEmpty(MatchingDbConnectionString))
        {
            return;
        }

        var sqlBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(TripDbConnectionString);
        var sqlHost = sqlBuilder.DataSource.Split(',')[0].Trim();
        if (sqlHost is not ("localhost" or "127.0.0.1") ||
            !sqlBuilder.InitialCatalog.Equals("swiftride_trip_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Trip test DB must run on localhost and be 'swiftride_trip_test'. Detected: {TripDbConnectionString}");
        }

        var payBuilder = new NpgsqlConnectionStringBuilder(PaymentDbConnectionString);
        if (payBuilder.Host is not ("localhost" or "127.0.0.1") ||
            !string.Equals(payBuilder.Database, "swiftride_payment_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Payment test DB must run on localhost and be 'swiftride_payment_test'. Detected: {PaymentDbConnectionString}");
        }

        var matchBuilder = new NpgsqlConnectionStringBuilder(MatchingDbConnectionString);
        if (matchBuilder.Host is not ("localhost" or "127.0.0.1") ||
            !string.Equals(matchBuilder.Database, "matching_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Matching test DB must run on localhost and be 'matching_test'. Detected: {MatchingDbConnectionString}");
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(TripDbConnectionString) ||
            string.IsNullOrEmpty(PaymentDbConnectionString) ||
            string.IsNullOrEmpty(MatchingDbConnectionString))
        {
            return;
        }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SwiftRide.sln"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Could not locate solution root directory.");

        // 1. Initialize SQL Server Trip DB
        var tripOptions = new DbContextOptionsBuilder<TripDbContext>()
            .UseSqlServer(TripDbConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var tripDb = new TripDbContext(tripOptions))
        {
            await tripDb.Database.MigrateAsync();
        }

        // 2. Initialize PostgreSQL Databases and Schemas
        var ddl1 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/matchdb-paydb.sql"));
        var ddl2 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/paydb-refunds.sql"));
        var ddl3 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/paydb-processing.sql"));
        var ddl4 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/paydb-outbox-inbox.sql"));

        await EnsurePostgresDatabaseCreatedAsync(PaymentDbConnectionString, "swiftride_payment_test");
        await EnsurePostgresDatabaseCreatedAsync(MatchingDbConnectionString, "matching_test");

        // Initialize Matching DB (matching_test -> matchdb schema)
        await using (var matchConn = new NpgsqlConnection(MatchingDbConnectionString))
        {
            await matchConn.OpenAsync();
            await using var cmd = matchConn.CreateCommand();
            cmd.CommandText = @"
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'swiftride_matching') THEN CREATE ROLE swiftride_matching NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN CREATE ROLE anon NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN CREATE ROLE authenticated NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN CREATE ROLE service_role NOLOGIN; END IF; END $$;
                GRANT swiftride_matching TO test_owner;
                CREATE SCHEMA IF NOT EXISTS matchdb;
                GRANT USAGE ON SCHEMA matchdb TO swiftride_matching;
                GRANT ALL ON SCHEMA matchdb TO test_owner;
            ";
            await cmd.ExecuteNonQueryAsync();

            var startMatch = ddl1.IndexOf("create table matchdb.driver_locations", StringComparison.Ordinal);
            var endMatch = ddl1.IndexOf("create table paydb.payments", StringComparison.Ordinal);
            var matchTablesSql = ddl1[startMatch..endMatch];

            try
            {
                await using var createTablesCmd = matchConn.CreateCommand();
                createTablesCmd.CommandText = matchTablesSql;
                await createTablesCmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P07") { /* already exists */ }
        }

        // Initialize Payment DB (swiftride_payment_test -> paydb schema)
        await using (var payConn = new NpgsqlConnection(PaymentDbConnectionString))
        {
            await payConn.OpenAsync();
            await using var cmd = payConn.CreateCommand();
            cmd.CommandText = @"
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'swiftride_payment') THEN CREATE ROLE swiftride_payment NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN CREATE ROLE anon NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN CREATE ROLE authenticated NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN CREATE ROLE service_role NOLOGIN; END IF; END $$;
                GRANT swiftride_payment TO test_owner;
                CREATE SCHEMA IF NOT EXISTS paydb;
                GRANT USAGE ON SCHEMA paydb TO swiftride_payment;
                GRANT ALL ON SCHEMA paydb TO test_owner;
            ";
            await cmd.ExecuteNonQueryAsync();

            var startPay = ddl1.IndexOf("create table paydb.payments", StringComparison.Ordinal);
            var payTablesSql = ddl1[startPay..];

            try
            {
                await using var payCmd = payConn.CreateCommand();
                payCmd.CommandText = payTablesSql;
                await payCmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }

            try
            {
                await using var refCmd = payConn.CreateCommand();
                refCmd.CommandText = ddl2;
                await refCmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }

            try
            {
                await using var procCmd = payConn.CreateCommand();
                procCmd.CommandText = ddl3;
                await procCmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }

            try
            {
                await using var outboxCmd = payConn.CreateCommand();
                outboxCmd.CommandText = ddl4;
                await outboxCmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }
        }

        await CleanAsync();
    }

    private static async Task EnsurePostgresDatabaseCreatedAsync(string targetCs, string dbName)
    {
        var adminCs = new NpgsqlConnectionStringBuilder(targetCs) { Database = "postgres" }.ToString();
        await using var adminConn = new NpgsqlConnection(adminCs);
        await adminConn.OpenAsync();
        await using var checkCmd = adminConn.CreateCommand();
        checkCmd.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{dbName}'";
        var exists = await checkCmd.ExecuteScalarAsync();
        if (exists is null)
        {
            await using var createCmd = adminConn.CreateCommand();
            createCmd.CommandText = $"CREATE DATABASE {dbName}";
            await createCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task CleanAsync()
    {
        if (string.IsNullOrEmpty(TripDbConnectionString) ||
            string.IsNullOrEmpty(PaymentDbConnectionString) ||
            string.IsNullOrEmpty(MatchingDbConnectionString))
        {
            return;
        }

        // Clean SQL Server Trip DB
        var tripOptions = new DbContextOptionsBuilder<TripDbContext>()
            .UseSqlServer(TripDbConnectionString)
            .Options;
        await using (var tripDb = new TripDbContext(tripOptions))
        {
            await tripDb.IntegrationInbox.ExecuteDeleteAsync();
            await tripDb.IntegrationOutbox.ExecuteDeleteAsync();
            await tripDb.TripPaymentJobs.ExecuteDeleteAsync();
            await tripDb.ActiveRiderTrips.ExecuteDeleteAsync();
            await tripDb.ActiveDriverAssignments.ExecuteDeleteAsync();
            await tripDb.CommandReceipts.ExecuteDeleteAsync();
            await tripDb.Trips.ExecuteDeleteAsync();
        }

        // Clean Matching DB
        var matchOptions = new DbContextOptionsBuilder<MatchingDbContext>()
            .UseNpgsql(MatchingDbConnectionString)
            .Options;
        await using (var matchDb = new MatchingDbContext(matchOptions))
        {
            await matchDb.DriverLocations.ExecuteDeleteAsync();
            await matchDb.Quotes.ExecuteDeleteAsync();
        }

        // Clean Payment DB (use TRUNCATE with session_replication_role = 'replica' to bypass append-only triggers)
        await using var payConn = new NpgsqlConnection(PaymentDbConnectionString);
        await payConn.OpenAsync();
        await using var payCmd = payConn.CreateCommand();
        payCmd.CommandText = @"
            SET session_replication_role = 'replica';
            TRUNCATE TABLE paydb.integration_inbox CASCADE;
            TRUNCATE TABLE paydb.integration_outbox CASCADE;
            TRUNCATE TABLE paydb.refund_ledger_entries CASCADE;
            TRUNCATE TABLE paydb.refunds CASCADE;
            TRUNCATE TABLE paydb.ledger_entries CASCADE;
            TRUNCATE TABLE paydb.payment_processing CASCADE;
            TRUNCATE TABLE paydb.mock_gateway_operations CASCADE;
            TRUNCATE TABLE paydb.payments CASCADE;
            SET session_replication_role = 'origin';
        ";
        await payCmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
