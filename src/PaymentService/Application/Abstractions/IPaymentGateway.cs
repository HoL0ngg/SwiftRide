using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.ValueObjects;
namespace SwiftRide.PaymentService.Application.Abstractions;

public enum GatewayOutcome { Succeeded = 1, Declined = 2, TransientFailure = 3 }
// Declined = lỗi vĩnh viễn; Transient = lỗi tạm, được retry

// FailureCode là MÃ ngắn (vd "card_declined") để lưu vào cột failure_code, không phải câu văn bản.
public sealed record GatewayResult(GatewayOutcome Outcome, string? Reference = null, string? FailureCode = null)
{
    public static GatewayResult Success(string reference) => new(GatewayOutcome.Succeeded, reference);
    public static GatewayResult Declined(string code) => new(GatewayOutcome.Declined, null, code);
    public static GatewayResult Transient(string code) => new(GatewayOutcome.TransientFailure, null, code);
}

// Token chỉ đi qua request này rồi bỏ; không lưu DB, không log
public sealed record GatewayChargeRequest(string IdempotencyKey, Money Amount, string PaymentToken);
public sealed record GatewayRefundRequest(string IdempotencyKey, string OriginalReference, Money Amount);

public interface IPaymentGateway    // Strategy: mỗi phương thức thanh toán là 1 chiến lược
{
    PaymentMethod Method { get; }
    Task<GatewayResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct);
    Task<GatewayResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct);
}
