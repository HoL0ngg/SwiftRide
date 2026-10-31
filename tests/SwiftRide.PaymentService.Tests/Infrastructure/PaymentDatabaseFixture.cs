using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftRide.PaymentService.Infrastructure.Persistence;

namespace SwiftRide.PaymentService.Tests.Infrastructure;

public sealed class LocalPostgresPaymentFactAttribute : FactAttribute
{
    public LocalPostgresPaymentFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PAYMENT_TEST_DB")))
        {
            Skip = "Set PAYMENT_TEST_DB to a disposable local PostgreSQL database.";
        }
    }
}

[CollectionDefinition("PaymentDatabase")]
public class PaymentDatabaseCollection : ICollectionFixture<PaymentDatabaseFixture>
{
}

public sealed class PaymentDatabaseFixture : IAsyncLifetime
{
    public string? ConnectionString { get; }

    public PaymentDatabaseFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("PAYMENT_TEST_DB");
        if (string.IsNullOrEmpty(ConnectionString))
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (builder.Host is not ("localhost" or "127.0.0.1"))
        {
            throw new InvalidOperationException($"Test database must run on localhost. Detected host: {builder.Host}");
        }

        if (!string.Equals(builder.Database, "swiftride_payment_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Test database name must be 'swiftride_payment_test'. Detected: {builder.Database}");
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            return;
        }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SwiftRide.sln"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Could not locate solution root directory.");

        var ddl1 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/matchdb-paydb.sql"));
        var ddl2 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/paydb-refunds.sql"));
        var ddl3 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/paydb-processing.sql"));
        var ddl4 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/paydb-outbox-inbox.sql"));
        var ddl5 = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/supabase/paydb-refund-processing.sql"));

        // Ensure database exists by connecting to postgres DB first
        var adminCs = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" }.ToString();
        await using (var adminConn = new NpgsqlConnection(adminCs))
        {
            await adminConn.OpenAsync();
            await using var checkCmd = adminConn.CreateCommand();
            checkCmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = 'swiftride_payment_test'";
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists is null)
            {
                await using var createCmd = adminConn.CreateCommand();
                createCmd.CommandText = "CREATE DATABASE swiftride_payment_test";
                await createCmd.ExecuteNonQueryAsync();
            }
        }

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // Ensure roles & clean schemas
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'swiftride_payment') THEN CREATE ROLE swiftride_payment NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'swiftride_matching') THEN CREATE ROLE swiftride_matching NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN CREATE ROLE anon NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN CREATE ROLE authenticated NOLOGIN; END IF; END $$;
                DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN CREATE ROLE service_role NOLOGIN; END IF; END $$;
                GRANT swiftride_payment TO test_owner;
                GRANT swiftride_matching TO test_owner;
                DROP SCHEMA IF EXISTS paydb CASCADE;
                DROP SCHEMA IF EXISTS matchdb CASCADE;
                CREATE SCHEMA paydb;
                CREATE SCHEMA matchdb;
            ";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException) { /* Concurrent role creation or schema init */ }

        var sanitizedDdl1 = ddl1
            .Replace("create schema matchdb;", "--")
            .Replace("create schema paydb;", "--")
            .Replace("create role swiftride_matching nologin;", "--")
            .Replace("create role swiftride_payment nologin;", "--");

        // Apply DDL scripts safely
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sanitizedDdl1;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl2;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl3;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl4;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl5;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P07" || ex.SqlState == "42710") { /* already exists */ }

        // Clean tables
        await ResetTablesAsync();
    }

    public async Task ResetTablesAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SET session_replication_role = 'replica';
            TRUNCATE TABLE paydb.integration_inbox CASCADE;
            TRUNCATE TABLE paydb.integration_outbox CASCADE;
            TRUNCATE TABLE paydb.refund_processing CASCADE;
            TRUNCATE TABLE paydb.refund_ledger_entries CASCADE;
            TRUNCATE TABLE paydb.refunds CASCADE;
            TRUNCATE TABLE paydb.ledger_entries CASCADE;
            TRUNCATE TABLE paydb.payment_processing CASCADE;
            TRUNCATE TABLE paydb.mock_gateway_operations CASCADE;
            TRUNCATE TABLE paydb.payments CASCADE;
            SET session_replication_role = 'origin';
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public PaymentDbContext CreateDbContext()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            throw new InvalidOperationException("PAYMENT_TEST_DB environment variable is not configured.");
        }

        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new PaymentDbContext(options);
    }
}
