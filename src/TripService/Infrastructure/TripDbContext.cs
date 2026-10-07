using Microsoft.EntityFrameworkCore;

namespace SwiftRide.TripService.Infrastructure;

public sealed class TripDbContext(DbContextOptions<TripDbContext> options) : DbContext(options)
{
}
