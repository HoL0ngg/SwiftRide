using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Infrastructure.Gateways;

public sealed class MockCardGateway() : MockPaymentGatewayBase(
    PaymentMethod.Card, "tok_card_ok", "tok_card_declined", "tok_card_timeout");   // 26.1 Token giả, không có số thẻ