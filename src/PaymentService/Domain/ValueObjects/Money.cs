using SwiftRide.PaymentService.Domain.Exceptions;

namespace SwiftRide.PaymentService.Domain.ValueObjects;

public sealed record Money   // record = so sánh theo giá trị, đúng bản chất Value Object
{
    public const string Vnd = "VND";
    public const decimal MaxExclusive = 1_000_000_000_000_000_000m;   // khớp CHECK amount < 1e18 của DB

    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = Vnd;

    private Money() { }                                         // cho EF Core

    public Money Copy() => new() { Amount = Amount, Currency = Currency };

    public static Money Zero => new() { Amount = 0m, Currency = Vnd };

    public static Money Create(decimal amount, string? currency)
    {
        if (!string.Equals(currency, Vnd, StringComparison.Ordinal))
            throw new DomainException("Only VND is supported.");
        if (amount < 0m)
            throw new DomainException("Amount must be greater than or equal to zero.");
        if (decimal.Truncate(amount) != amount)
            throw new DomainException("VND amounts must be whole numbers.");
        if (amount >= MaxExclusive)
            throw new DomainException("Amount is too large.");
        return new Money { Amount = amount, Currency = Vnd };
    }
}
