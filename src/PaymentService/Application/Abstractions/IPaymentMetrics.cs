namespace SwiftRide.PaymentService.Application.Abstractions;

public interface IPaymentMetrics
{
    void PaymentSettled();
    void PaymentFailed();
    void PaymentRefunded();
}

public sealed class NullPaymentMetrics : IPaymentMetrics
{
    public static readonly NullPaymentMetrics Instance = new();
    public void PaymentSettled() { }
    public void PaymentFailed() { }
    public void PaymentRefunded() { }
}
