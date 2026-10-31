namespace SwiftRide.TripService.Application.Payments;

public sealed class PaymentWorkflowOptions
{
    public int MaxAttempts { get; set; } = 20;
    public TimeSpan TotalBudget { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);
    public string DefaultCardAlias { get; set; } = "card.success.v1";
    public string DefaultWalletAlias { get; set; } = "wallet.success.v1";
}
