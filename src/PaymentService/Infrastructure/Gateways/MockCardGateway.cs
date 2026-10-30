using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Enums;

namespace SwiftRide.PaymentService.Infrastructure.Gateways;

public sealed class MockCardGateway(IMockOperationStore? mockStore = null) : MockPaymentGatewayBase(
    PaymentMethod.Card, "tok_card_ok", "tok_card_declined", "tok_card_timeout", mockStore);
