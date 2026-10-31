namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class RefundProcessingRecord
{
    public Guid RefundId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid TripId { get; set; }
    public string PaymentKey { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public int AttemptsStarted { get; set; }
    public Guid? OwnerId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string Outcome { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
