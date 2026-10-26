using SwiftRide.PaymentService.Domain.Exceptions;

namespace SwiftRide.PaymentService.Domain.ValueObjects;

public sealed record Money   // 3.1 record = so sánh theo giá trị, đúng bản chất Value Object
{
    public const string Vnd = "VND";
    public decimal Amount { get; private set; }                 // 3.2 private set: chỉ Create() được gán
    public string Currency { get; private set; } = Vnd;

    private Money() { }                                         // 3.3 Constructor rỗng cho EF Core

    public Money Copy() => new() { Amount = Amount, Currency = Currency };

    public static Money Create(decimal amount, string? currency)
    {
        if (!string.Equals(currency, Vnd, StringComparison.Ordinal))
            throw new DomainException("Only VND is supported.");              // 3.4 AGENTS: tiền VND
        if (amount <= 0)
            throw new DomainException("Amount must be greater than zero.");   // 3.5 Không bao giờ tạo được tiền <= 0
        if (decimal.Truncate(amount) != amount)
            throw new DomainException("VND amounts must be whole numbers.");  // 3.6 VND không có số lẻ
        return new Money { Amount = amount, Currency = Vnd };
    }
}