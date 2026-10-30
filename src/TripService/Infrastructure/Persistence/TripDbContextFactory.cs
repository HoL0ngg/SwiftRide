using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class TripDbContextFactory : IDesignTimeDbContextFactory<TripDbContext>
{
    public TripDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TripDbContext>();
        optionsBuilder.UseSqlServer("Server=localhost;Database=TripDB;TrustServerCertificate=True;");
        return new TripDbContext(optionsBuilder.Options);
    }
}
