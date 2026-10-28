using Microsoft.EntityFrameworkCore;

namespace SwiftRide.MatchingService.Infrastructure.Persistence;

public sealed class DriverLocationRow
{
    public string DriverId { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool IsOnline { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class QuoteRow
{
    public Guid Id { get; set; }
    public decimal DistanceKm { get; set; }
    public decimal EstimatedMinutes { get; set; }
    public decimal CostPerKm { get; set; }
    public decimal CostPerMinute { get; set; }
    public decimal Surge { get; set; }
    public decimal Toll { get; set; }
    public decimal PromotionFactor { get; set; }
    public decimal Tax { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "VND";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class MatchingDbContext(DbContextOptions<MatchingDbContext> options) : DbContext(options)
{
    public DbSet<DriverLocationRow> DriverLocations => Set<DriverLocationRow>();
    public DbSet<QuoteRow> Quotes => Set<QuoteRow>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var d = modelBuilder.Entity<DriverLocationRow>();
        d.ToTable("driver_locations", "matchdb");
        d.HasKey(x => x.DriverId);
        d.Property(x => x.DriverId).HasColumnName("driver_id");
        d.Property(x => x.Latitude).HasColumnName("latitude");
        d.Property(x => x.Longitude).HasColumnName("longitude");
        d.Property(x => x.IsOnline).HasColumnName("is_online");
        d.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        var q = modelBuilder.Entity<QuoteRow>();
        q.ToTable("quotes", "matchdb");
        q.HasKey(x => x.Id);
        q.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        q.Property(x => x.DistanceKm).HasColumnName("distance_km");
        q.Property(x => x.EstimatedMinutes).HasColumnName("estimated_minutes");
        q.Property(x => x.CostPerKm).HasColumnName("cost_per_km");
        q.Property(x => x.CostPerMinute).HasColumnName("cost_per_minute");
        q.Property(x => x.Surge).HasColumnName("surge");
        q.Property(x => x.Toll).HasColumnName("toll");
        q.Property(x => x.PromotionFactor).HasColumnName("promotion_factor");
        q.Property(x => x.Tax).HasColumnName("tax");
        q.Property(x => x.Amount).HasColumnName("amount");
        q.Property(x => x.Currency).HasColumnName("currency");
        q.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}
