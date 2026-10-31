using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Application.Messaging;

namespace SwiftRide.PaymentService.Infrastructure.Messaging;

public sealed class NoOpPaymentEventWriter : IPaymentEventWriter
{
    public void Add(PaymentSettledV1 message) { }
    public void Add(PaymentFailedV1 message) { }
    public void Add(PaymentRefundedV1 message) { }
    public void Add(PaymentCompensationFailedV1 message) { }
}
