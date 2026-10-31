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

    // Canonical fingerprint v2. Không lưu raw token; dùng SHA256 digest của token.
    // DB có CHECK request_fingerprint ~ '^[0-9a-f]{64}$' => PHẢI là hex chữ thường 64 ký tự.
    // Định dạng canonical có tiền tố độ dài (length-prefixed) để loại trừ triệt để xung đột delimiter '|'.
    public static string Fingerprint(Guid tripId, string riderId, string driverId, Money amount, PaymentMethod method, string paymentToken)
    {
        var normalizedToken = (paymentToken ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(normalizedToken))
            throw new DomainException("PaymentToken is required.");

        var tokenDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedToken)));

        var parts = new[]
        {
            "v2",
            tripId.ToString("D", CultureInfo.InvariantCulture),
            (riderId ?? string.Empty).Trim(),
            (driverId ?? string.Empty).Trim(),
            amount.Amount.ToString("0", CultureInfo.InvariantCulture),
            (amount.Currency ?? string.Empty).Trim(),
            method.ToString(),
            tokenDigest
        };

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(part.Length).Append(':').Append(part).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static string RefundFingerprint(Guid paymentId, Money amount, string reason)
    {
        var parts = new[]
        {
            "v2",
            paymentId.ToString("D", CultureInfo.InvariantCulture),
            amount.Amount.ToString("0", CultureInfo.InvariantCulture),
            (amount.Currency ?? string.Empty).Trim(),
            (reason ?? string.Empty).Trim()
        };

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(part.Length).Append(':').Append(part).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
