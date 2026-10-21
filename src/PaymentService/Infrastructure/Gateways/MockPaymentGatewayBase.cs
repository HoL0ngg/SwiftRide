using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Infrastructure.Gateways;

// 25.1 Template Method: khung xử lý token giống nhau, lớp con chỉ khai báo token của mình
public abstract class MockPaymentGatewayBase(PaymentMethod method, string successToken,
    string declinedToken, string timeoutToken) : IPaymentGateway
{
    public PaymentMethod Method => method;

    public Task<GatewayResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result =
            request.PaymentToken == successToken ? GatewayResult.Success($"mock-charge-{request.IdempotencyKey}")
            : request.PaymentToken == declinedToken ? GatewayResult.Declined("Payment method was declined.")
            : request.PaymentToken == timeoutToken ? GatewayResult.Transient("Mock gateway is temporarily unavailable.")
            : GatewayResult.Declined("Unsupported payment token.");
        return Task.FromResult(result);
    }

    public Task<GatewayResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GatewayResult.Success($"mock-refund-{request.IdempotencyKey}")); // 25.2 Mock: hoàn luôn thành công
    }
}