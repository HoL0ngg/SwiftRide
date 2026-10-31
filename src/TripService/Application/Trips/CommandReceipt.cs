namespace SwiftRide.TripService.Application.Trips;

public sealed record CommandScope(string ActorId, string Role, string Operation, Guid? TripId)
{
    public string ToScopeString() => $"{Role.ToLowerInvariant()}:{ActorId}:{Operation.ToLowerInvariant()}:{TripId?.ToString("D") ?? "root"}";
}

public sealed class CommandReceipt
{
    public CommandReceipt(
        string scope,
        string key,
        string requestHash,
        string? responseJson,
        string status,
        int attempts,
        DateTimeOffset? nextAttemptUtc,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Scope = scope;
        Key = key;
        RequestHash = requestHash;
        ResponseJson = responseJson;
        Status = status;
        Attempts = attempts;
        NextAttemptUtc = nextAttemptUtc;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public string Scope { get; }
    public string Key { get; }
    public string RequestHash { get; set; }
    public string? ResponseJson { get; set; }
    public string Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }
}
