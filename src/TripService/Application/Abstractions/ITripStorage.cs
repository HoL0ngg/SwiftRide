using SwiftRide.TripService.Application.Trips;

namespace SwiftRide.TripService.Application.Abstractions;

public sealed record DriverFeedReadModel(TripContext? OfferedTrip, TripContext? ActiveTrip);
public sealed record TripHistoryPageReadModel(IReadOnlyList<TripContext> Items, string? NextCursor);

public interface ITripReader
{
    Task<TripContext?> LoadAsync(Guid id, CancellationToken ct);
}

public interface ITripWriter
{
    void Add(TripContext trip);
    void Update(TripContext trip);
}

public interface ITripUnitOfWork
{
    Task SaveAsync(CancellationToken ct);
}

public interface ICommandReceiptStore
{
    Task<CommandReceipt?> FindAsync(CommandScope scope, string key, CancellationToken ct);
    void Add(CommandReceipt receipt);
    void Update(CommandReceipt receipt);
}

public interface IActiveAssignmentStore
{
    void ClaimRider(string riderId, Guid tripId);
    void ReleaseRider(Guid tripId);
    void ClaimDriver(string driverId, Guid tripId);
    bool TryClaimDriver(string driverId, Guid tripId);
    void ReleaseDriver(Guid tripId);
}

public interface ITripWorkStore
{
    Task<TripWorkLease?> TryClaimAsync(TripWorkKind kind, Guid owner, DateTimeOffset now, CancellationToken ct);
    void StageCompletion(TripWorkLease lease);
    void StageRetry(TripWorkLease lease, DateTimeOffset nextAttempt, string reasonCode);
    void StageRequiresAction(TripWorkLease lease, string reasonCode);
    void AddPaymentJob(Guid tripId, string paymentKey, DateTimeOffset now, int maxAttempts, DateTimeOffset deadlineUtc, string transport);

    void StageUnownedCompletion(Guid tripId);
    void StageUnownedRetry(Guid tripId, DateTimeOffset nextAttempt, string reasonCode);
    void StageUnownedRequiresAction(Guid tripId, string reasonCode);
}

public interface ITripQueryReader
{
    Task<TripContext?> GetCurrentAsync(TripActor actor, CancellationToken ct);
    Task<DriverFeedReadModel> GetDriverFeedAsync(TripActor actor, CancellationToken ct);
    Task<TripHistoryPageReadModel> GetHistoryAsync(TripActor actor, string? cursor, int limit, CancellationToken ct);
}
