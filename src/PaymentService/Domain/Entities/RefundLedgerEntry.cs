using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Domain.Entities;

// Bảng paydb.refund_ledger_entries. Khóa chính (refund_id, side). Bút toán đảo của lần thu tiền.
public sealed class RefundLedgerEntry
{
    public Guid RefundId { get; private set; }
    public EntryDirection Direction { get; private set; }
    public string Account { get; private set; } = default!;
    public Money Amount { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private RefundLedgerEntry() { }

    internal static RefundLedgerEntry Create(Guid refundId, string account, EntryDirection direction,
        Money amount, DateTimeOffset now) => new()
        {
            RefundId = refundId,
            Account = account,
            Direction = direction,
            Amount = amount,
            CreatedAt = now
        };
}
