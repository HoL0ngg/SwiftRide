namespace SwiftRide.PaymentService.Application.Payments;

public sealed class PaymentProcessingSettings
{
    public const string SectionName = "PaymentProcessing";

    public int MaxAttempts { get; set; } = 3;
    public int LeaseSeconds { get; set; } = 60;
    public int PendingLeaseSeconds { get; set; } = 30;

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan PendingLease => TimeSpan.FromSeconds(PendingLeaseSeconds);
}