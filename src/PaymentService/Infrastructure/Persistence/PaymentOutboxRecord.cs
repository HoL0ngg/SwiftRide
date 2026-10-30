namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentOutboxRecord
{
    public Guid MessageId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 10;
    public Guid? Owner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string? LastError { get; set; }
    public bool RequiresAction { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public Guid? CausationId { get; set; }
    public string? TraceParent { get; set; }
}
