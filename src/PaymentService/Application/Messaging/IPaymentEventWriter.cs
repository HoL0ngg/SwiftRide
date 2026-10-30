using SwiftRide.Contracts.V1;

namespace SwiftRide.PaymentService.Application.Messaging;

public interface IPaymentEventWriter
{
    void Add(PaymentSettledV1 message);
    void Add(PaymentFailedV1 message);
    void Add(PaymentRefundedV1 message);
    void Add(PaymentCompensationFailedV1 message);
}
