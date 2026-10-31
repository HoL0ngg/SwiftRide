using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;
using SwiftRide.PaymentService.Infrastructure.Persistence;
using SwiftRide.PaymentService.Tests.Infrastructure;
using Xunit;

namespace SwiftRide.PaymentService.Tests;

[Collection("PaymentDatabase")]
public class PaymentDatabaseTests
{
    private readonly PaymentDatabaseFixture _fixture;

    public PaymentDatabaseTests(PaymentDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [LocalPostgresPaymentFact]
    public async Task Schema_InsertPending_ThenSettleWithBalancedLedger_Succeeds()
    {
        await _fixture.ResetTablesAsync();
        await using var db = _fixture.CreateDbContext();

        var tripId = Guid.NewGuid();
        var payment = Payment.Create(tripId, "rider-1", "driver-1", Money.Create(50_000m, "VND"),
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(tripId), "tok_card_ok", DateTimeOffset.UtcNow);

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        payment.MarkSettled("gw-ref-123", DateTimeOffset.UtcNow);
        db.LedgerEntries.AddRange(payment.LedgerEntries);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.Payments.Include(p => p.LedgerEntries).FirstAsync(p => p.Id == payment.Id);

        Assert.Equal(PaymentStatus.Settled, reloaded.Status);
        Assert.Equal("gw-ref-123", reloaded.GatewayReference);
        Assert.Equal(2, reloaded.LedgerEntries.Count);
        Assert.True(reloaded.IsLedgerBalanced());
    }

    [LocalPostgresPaymentFact]
    public async Task Schema_UniqueConstraint_OnPaymentIdempotencyKey_ThrowsDuplicate()
    {
        await _fixture.ResetTablesAsync();
        await using var db1 = _fixture.CreateDbContext();
        await using var db2 = _fixture.CreateDbContext();

        var tripId = Guid.NewGuid();
        var key = PaymentIdempotency.KeyForTrip(tripId);

        var p1 = Payment.Create(tripId, "rider-1", "driver-1", Money.Create(50_000m, "VND"),
            PaymentMethod.Card, key, "tok_card_ok", DateTimeOffset.UtcNow);
        var p2 = Payment.Create(tripId, "rider-1", "driver-1", Money.Create(50_000m, "VND"),
            PaymentMethod.Card, key, "tok_card_ok", DateTimeOffset.UtcNow);

        db1.Payments.Add(p1);
        await db1.SaveChangesAsync();

        db2.Payments.Add(p2);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
    }

    [LocalPostgresPaymentFact]
    public async Task Schema_AppendOnly_UpdatingOrDeletingLedgerEntries_Throws()
    {
        await _fixture.ResetTablesAsync();
        await using var db = _fixture.CreateDbContext();

        var tripId = Guid.NewGuid();
        var payment = Payment.Create(tripId, "rider-1", "driver-1", Money.Create(50_000m, "VND"),
            PaymentMethod.Card, PaymentIdempotency.KeyForTrip(tripId), "tok_card_ok", DateTimeOffset.UtcNow);

        db.Payments.Add(payment);
        payment.MarkSettled("gw-ref-1", DateTimeOffset.UtcNow);
        db.LedgerEntries.AddRange(payment.LedgerEntries);
        await db.SaveChangesAsync();

        // Attempt direct UPDATE via SQL to test trigger ledger_append_only
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE paydb.ledger_entries SET amount = 99999 WHERE payment_id = {0}", payment.Id);
        });

        Assert.Equal("23514", ex.SqlState); // Check violation trigger
        Assert.Contains("append-only", ex.MessageText);
    }

    [LocalPostgresPaymentFact]
    public async Task Schema_Isolation_PaymentRoleCannotQueryMatchDb()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SET ROLE swiftride_payment; SELECT * FROM matchdb.driver_locations;";

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }
}
