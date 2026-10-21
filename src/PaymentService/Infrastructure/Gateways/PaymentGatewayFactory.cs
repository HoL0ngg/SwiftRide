using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
namespace SwiftRide.PaymentService.Infrastructure.Gateways;

public sealed class PaymentGatewayFactory(IEnumerable<IPaymentGateway> gateways) : IPaymentGatewayFactory
{
    private readonly Dictionary<PaymentMethod, IPaymentGateway> _byMethod = gateways.ToDictionary(g => g.Method); // 27.1 DI tự gom mọi gateway đã đăng ký

    public IPaymentGateway Create(PaymentMethod method) =>
        _byMethod.TryGetValue(method, out var gateway)
            ? gateway
            : throw new DomainException($"Unsupported payment method '{method}'.");   // 27.2 OCP: thêm gateway mới KHÔNG sửa factory
}