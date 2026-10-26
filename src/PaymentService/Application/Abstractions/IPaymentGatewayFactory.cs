using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Application.Abstractions;

public interface IPaymentGatewayFactory { IPaymentGateway Create(PaymentMethod method); }
// 9.1 Factory theo AGENTS