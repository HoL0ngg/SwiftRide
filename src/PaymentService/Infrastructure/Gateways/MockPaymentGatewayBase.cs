using System.Collections.Concurrent;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Infrastructure.Gateways;

public abstract class MockPaymentGatewayBase(PaymentMethod method, string successToken,
    string declinedToken, string timeoutToken) : IPaymentGateway
{
    // Receipt theo idempotency key: gọi lại cùng key trả đúng kết quả cũ, không "charge" lần 2.
    // Chỉ sống trong process (mất khi restart). Muốn bền qua restart thì lưu vào bảng/file.
    private readonly ConcurrentDictionary<string, GatewayResult> _receipts = new();

    public PaymentMethod Method => method;

    public Task<GatewayResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result =
            request.PaymentToken == successToken ? GatewayResult.Success($"mock-charge-{request.IdempotencyKey}")
            : request.PaymentToken == declinedToken ? GatewayResult.Declined("payment_method_declined")
            : request.PaymentToken == timeoutToken ? GatewayResult.Transient("gateway_unavailable")
            : GatewayResult.Declined("unsupported_token");
        return Task.FromResult(Remember(request.IdempotencyKey, result));
    }

    public Task<GatewayResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = GatewayResult.Success($"mock-refund-{request.IdempotencyKey}");
        return Task.FromResult(Remember(request.IdempotencyKey, result));
    }

    // Lỗi tạm KHÔNG lưu receipt (phải retry được); thành công/từ chối thì lưu, ai gọi sau nhận lại bản đầu
    private GatewayResult Remember(string key, GatewayResult result) =>
        result.Outcome == GatewayOutcome.TransientFailure ? result : _receipts.GetOrAdd(key, result);
}