using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Domain.Entities;

// Bảng paydb.refunds (tối đa 1 dòng / payment: UNIQUE payment_id). Thuộc aggregate Payment,
// nên chỉ Payment mới tạo/đổi trạng thái được (hàm internal).
public sealed class Refund
{
    private readonly List<RefundLedgerEntry> _ledgerEntries = [];

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public Money Amount { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public string RequestFingerprint { get; private set; } = default!;
    public string Reason { get; private set; } = default!;
    public RefundStatus Status { get; private set; }
    public string? GatewayReference { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }
    public IReadOnlyCollection<RefundLedgerEntry> LedgerEntries => _ledgerEntries;

    private Refund() { }

    internal static string NormalizeReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 500)      // DB: 1..500 sau khi trim
            throw new DomainException("Refund reason is required (max 500 characters).");
        return trimmed;
    }

    internal static Refund Create(Guid paymentId, Money amount, string reason, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        PaymentId = paymentId,
        Amount = amount.Copy(),
        IdempotencyKey = PaymentIdempotency.KeyForRefund(paymentId),
        RequestFingerprint = PaymentIdempotency.RefundFingerprint(paymentId, amount, reason),
        Reason = reason,
        Status = RefundStatus.Pending,
        CreatedAt = now,
        UpdatedAt = now
    };

    // Pending -> Settled, đồng thời ghi 2 dòng sổ cái đảo (Nợ tài xế, Có rider)
    internal void MarkSettled(string gatewayReference, string debitAccount, string creditAccount, DateTimeOffset now)
    {
        if (Status != RefundStatus.Pending)
            throw new InvalidPaymentStateException($"Refund must be Pending but is {Status}.");
        if (string.IsNullOrWhiteSpace(gatewayReference))
            throw new DomainException("Gateway reference is required.");

        Status = RefundStatus.Settled;
        GatewayReference = gatewayReference;
        SettledAt = now;                  // DB: (status='settled') = (settled_at IS NOT NULL)
        UpdatedAt = now;
        _ledgerEntries.Add(RefundLedgerEntry.Create(Id, debitAccount, EntryDirection.Debit, Amount.Copy(), now));
        _ledgerEntries.Add(RefundLedgerEntry.Create(Id, creditAccount, EntryDirection.Credit, Amount.Copy(), now));
    }
}
