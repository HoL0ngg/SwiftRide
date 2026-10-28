using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Domain.Services;

public static class PaymentIdempotency
{
    // DB có CHECK: idempotency_key = 'trip-' || trip_id || '-payment'  (payments)
    //              idempotency_key = 'payment-' || payment_id || '-refund' (refunds)
    public static string KeyForTrip(Guid tripId) => $"trip-{tripId:D}-payment";
    public static string KeyForRefund(Guid paymentId) => $"payment-{paymentId:D}-refund";

    public static string EnsureValid(string? key, Guid tripId)
    {
        var expected = KeyForTrip(tripId);
        if (!string.Equals(key, expected, StringComparison.Ordinal))
            throw new DomainException($"Idempotency key must be '{expected}'.");
        return expected;
    }

    // "Dấu vân tay" payload. Không đưa token vào (không lưu token).
    // DB có CHECK request_fingerprint ~ '^[0-9a-f]{64}$' => PHẢI là hex chữ thường.
    public static string Fingerprint(Guid tripId, string riderId, string driverId, Money amount, PaymentMethod method) =>
        Hash(tripId, riderId, driverId, amount.Amount.ToString("0", CultureInfo.InvariantCulture), amount.Currency, method);

    public static string RefundFingerprint(Guid paymentId, Money amount, string reason) =>
        Hash(paymentId, amount.Amount.ToString("0", CultureInfo.InvariantCulture), amount.Currency, reason);

    private static string Hash(params object[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))));
}
