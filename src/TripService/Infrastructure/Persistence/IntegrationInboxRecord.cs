namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class IntegrationInboxRecord
{
    public string ConsumerName { get; set; } = string.Empty;
    public Guid MessageId { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? LastError { get; set; }
}
