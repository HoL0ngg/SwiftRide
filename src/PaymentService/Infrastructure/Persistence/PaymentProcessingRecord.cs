namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class PaymentProcessingRecord
{
    public Guid PaymentId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public int Version { get; set; } = 2;
    public string DriverId { get; set; } = string.Empty;
    public int AttemptsStarted { get; set; }
    public Guid? OwnerId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string Outcome { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
