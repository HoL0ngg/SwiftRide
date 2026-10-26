using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Infrastructure.Gateways;

public sealed class MockWalletGateway() : MockPaymentGatewayBase(
    PaymentMethod.Wallet, "tok_wallet_ok", "tok_wallet_insufficient", "tok_wallet_timeout");