using SwiftRide.TripService.Application.Abstractions;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class TripUnitOfWork : ITripUnitOfWork
{
    private readonly TripDbContext _db;

    public TripUnitOfWork(TripDbContext db)
    {
        _db = db;
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
    }
}
