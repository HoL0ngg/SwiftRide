using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Domain.Services;

public static class PaymentIdempotency
{
    public static string KeyForTrip(Guid tripId) => $"trip-{tripId:D}-payment";   // 4.1 Key xác định (deterministic) theo AGENTS

    public static string EnsureValid(string? key, Guid tripId)
    {
        var expected = KeyForTrip(tripId);
        if (!string.Equals(key, expected, StringComparison.Ordinal))              // 4.2 Chặn dùng key của chuyến khác
            throw new DomainException($"Idempotency key must be '{expected}'.");
        return expected;
    }

    // 4.3 "Dấu vân tay" của payload: cùng key + cùng fingerprint = trả kết quả cũ; khác = 409
    // 4.4 KHÔNG đưa token vào (không lưu token), nên chỉ băm các trường nghiệp vụ
    public static string Fingerprint(Guid tripId, Guid riderId, Guid driverId, Money amount, PaymentMethod method)
    {
        var canonical = string.Join('|', tripId, riderId, driverId,
            amount.Amount.ToString("0", CultureInfo.InvariantCulture), amount.Currency, method);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));  // 4.5 64 ký tự hex
    }
}