using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Domain.Entities;

// Bảng paydb.ledger_entries. Khóa chính (payment_id, side): mỗi payment đúng 1 dòng Nợ + 1 dòng Có.
// Bất biến: không có hàm sửa => append-only.
public sealed class LedgerEntry
{
    public Guid PaymentId { get; private set; }
    public EntryDirection Direction { get; private set; }         // cột "side"
    public string Account { get; private set; } = default!;       // cột "account_code", vd "rider:abc", "driver:xyz"
    public Money Amount { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private LedgerEntry() { }

    // internal: chỉ aggregate Payment (cùng assembly) mới được tạo dòng sổ cái
    internal static LedgerEntry Create(Guid paymentId, string account, EntryDirection direction,
        Money amount, DateTimeOffset now) => new()
        {
            PaymentId = paymentId,
            Account = account,
            Direction = direction,
            Amount = amount,
            CreatedAt = now
        };
}
