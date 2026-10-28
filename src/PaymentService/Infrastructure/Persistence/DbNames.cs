namespace SwiftRide.PaymentService.Infrastructure.Persistence;

// Tên schema / constraint phải khớp ĐÚNG với database trên Supabase.
public static class DbNames
{
    public const string Schema = "paydb";

    // UNIQUE constraint: vi phạm = "request này đã có người xử lý trước" (không phải lỗi hệ thống)
    public const string PaymentIdempotencyKey = "payments_idempotency_key_key";
    public const string PaymentTripId = "payments_trip_id_key";
    public const string RefundIdempotencyKey = "refunds_idempotency_key_key";
    public const string RefundPaymentId = "refunds_payment_id_key";

    public static readonly string[] DuplicateRequestConstraints =
        [PaymentIdempotencyKey, PaymentTripId, RefundIdempotencyKey, RefundPaymentId];
}
