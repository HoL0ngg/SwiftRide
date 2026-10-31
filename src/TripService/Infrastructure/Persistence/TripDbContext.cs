using Microsoft.EntityFrameworkCore;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public class TripDbContext : DbContext
{
    public TripDbContext(DbContextOptions options) : base(options) { }

    public DbSet<TripRecord> Trips => Set<TripRecord>();
    public DbSet<TripDriverRejectionRecord> TripDriverRejections => Set<TripDriverRejectionRecord>();
    public DbSet<ActiveRiderTripRecord> ActiveRiderTrips => Set<ActiveRiderTripRecord>();
    public DbSet<ActiveDriverAssignmentRecord> ActiveDriverAssignments => Set<ActiveDriverAssignmentRecord>();
    public DbSet<CommandReceiptRecord> CommandReceipts => Set<CommandReceiptRecord>();
    public DbSet<TripPaymentJobRecord> TripPaymentJobs => Set<TripPaymentJobRecord>();
    public DbSet<IntegrationOutboxRecord> IntegrationOutbox => Set<IntegrationOutboxRecord>();
    public DbSet<IntegrationInboxRecord> IntegrationInbox => Set<IntegrationInboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TripDbContext).Assembly);
    }
}
