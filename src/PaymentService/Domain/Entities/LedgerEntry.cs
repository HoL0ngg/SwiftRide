using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Domain.Entities;

public sealed class LedgerEntry                 // 5.1 Bất biến: không có hàm sửa => append-only
{
    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public string Account { get; private set; } = default!;   // 5.2 vd "rider:{id}", "driver:{id}"
    public EntryDirection Direction { get; private set; }
    public LedgerEntryType Type { get; private set; }
    public Money Amount { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private LedgerEntry() { }

    // 5.3 internal: chỉ aggregate Payment (cùng assembly) mới được tạo dòng sổ cái
    internal static LedgerEntry Create(Guid paymentId, string account, EntryDirection direction,
        LedgerEntryType type, Money amount, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            PaymentId = paymentId,
            Account = account,
            Direction = direction,
            Type = type,
            Amount = amount,
            CreatedAt = now
        };
}