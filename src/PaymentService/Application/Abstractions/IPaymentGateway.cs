using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.ValueObjects;
namespace SwiftRide.PaymentService.Application.Abstractions;

public enum GatewayOutcome { Succeeded = 1, Declined = 2, TransientFailure = 3 }
// 8.1 Declined = lỗi vĩnh viễn; Transient = lỗi tạm, được retry

public sealed record GatewayResult(GatewayOutcome Outcome, string? Reference = null, string? FailureReason = null)
{
    public static GatewayResult Success(string reference) => new(GatewayOutcome.Succeeded, reference);
    public static GatewayResult Declined(string reason) => new(GatewayOutcome.Declined, null, reason);
    public static GatewayResult Transient(string reason) => new(GatewayOutcome.TransientFailure, null, reason);
}

// 8.2 Token chỉ đi qua request này rồi bỏ; không lưu DB, không log
public sealed record GatewayChargeRequest(string IdempotencyKey, Money Amount, string PaymentToken);
public sealed record GatewayRefundRequest(string IdempotencyKey, string OriginalReference, Money Amount);

public interface IPaymentGateway    // 8.3 Strategy: mỗi phương thức thanh toán là 1 chiến lược
{
    PaymentMethod Method { get; }
    Task<GatewayResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct);
    Task<GatewayResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct);
}