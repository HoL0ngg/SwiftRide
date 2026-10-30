using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Enums;

namespace SwiftRide.PaymentService.Infrastructure.Gateways;

public sealed class MockWalletGateway(IMockOperationStore? mockStore = null) : MockPaymentGatewayBase(
    PaymentMethod.Wallet, "tok_wallet_ok", "tok_wallet_declined", "tok_wallet_timeout", mockStore);
