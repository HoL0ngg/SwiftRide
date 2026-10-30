using SwiftRide.PaymentService.Application.Abstractions;

namespace SwiftRide.PaymentService.Api.Observability;

public sealed class PaymentMetrics : IPaymentMetrics
{
    public void PaymentSettled() => PaymentTelemetry.PaymentsSettled.Add(1);
    public void PaymentFailed() => PaymentTelemetry.PaymentsFailed.Add(1);
    public void PaymentRefunded() => PaymentTelemetry.PaymentsRefunded.Add(1);
}
