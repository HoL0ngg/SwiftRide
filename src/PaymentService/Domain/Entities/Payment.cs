using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Domain.Entities;

public sealed class Payment   // 6.1 Aggregate root: Payment + LedgerEntries luôn lưu chung 1 transaction
{
    private readonly List<LedgerEntry> _ledgerEntries = [];

    public Guid Id { get; private set; }
    public Guid TripId { get; private set; }
    public Guid RiderId { get; private set; }
    public Guid DriverId { get; private set; }
    public Money Amount { get; private set; } = default!;      // 6.2 Số tiền Trip gửi sang, KHÔNG tính lại giá
    public PaymentMethod Method { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string IdempotencyKey { get; private set; } = default!;
    public string RequestFingerprint { get; private set; } = default!;
    public string? GatewayReference { get; private set; }     // 6.3 Mã giao dịch từ cổng (không phải số thẻ/token)
    public string? FailureReason { get; private set; }
    public string? RefundReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }
    public IReadOnlyCollection<LedgerEntry> LedgerEntries => _ledgerEntries;  // 6.4 Chỉ đọc từ ngoài

    private string RiderAccount => $"rider:{RiderId:D}";
    private string DriverAccount => $"driver:{DriverId:D}";

    private Payment() { }

    public static Payment Create(Guid tripId, Guid riderId, Guid driverId, Money amount,
        PaymentMethod method, string? idempotencyKey, DateTimeOffset now)
    {
        if (tripId == Guid.Empty || riderId == Guid.Empty || driverId == Guid.Empty)
            throw new DomainException("TripId, RiderId and DriverId are required.");
        if (riderId == driverId)
            throw new DomainException("Rider and driver must be different users.");
        if (!Enum.IsDefined(method))
            throw new DomainException("Unsupported payment method.");
        var key = PaymentIdempotency.EnsureValid(idempotencyKey, tripId);       // 6.5 Validate key TRƯỚC mọi lời gọi mạng

        return new Payment
        {
            Id = Guid.CreateVersion7(),
            TripId = tripId,
            RiderId = riderId,
            DriverId = driverId,
            Amount = amount,
            Method = method,
            Status = PaymentStatus.Pending,
            IdempotencyKey = key,
            RequestFingerprint = PaymentIdempotency.Fingerprint(tripId, riderId, driverId, amount, method),
            CreatedAt = now
        };
    }

    public bool HasSameRequestAs(Payment other) =>                               // 6.6 Cùng key + cùng payload?
        string.Equals(RequestFingerprint, other.RequestFingerprint, StringComparison.Ordinal);

    public void MarkSettled(string gatewayReference, DateTimeOffset now)         // 6.7 Pending -> Settled
    {
        EnsureStatus(PaymentStatus.Pending); // sai trạng thái → InvalidPaymentStateException
        if (string.IsNullOrWhiteSpace(gatewayReference))
            throw new DomainException("Gateway reference is required.");
        Status = PaymentStatus.Settled;
        GatewayReference = gatewayReference;
        CompletedAt = now;
        Post(LedgerEntryType.Charge, RiderAccount, DriverAccount, now);          // 6.8 Rider ghi Nợ, Driver ghi Có
    }

    public void MarkFailed(string reason, DateTimeOffset now)                    // 6.9 Pending -> Failed (không ghi sổ cái)
    {
        EnsureStatus(PaymentStatus.Pending); // sai trạng thái → InvalidPaymentStateException
        Status = PaymentStatus.Failed;
        FailureReason = reason.Length > 500 ? reason[..500] : reason;
        CompletedAt = now;
    }

    public void Refund(string? reason, DateTimeOffset now)                       // 6.10 Settled -> Refunded
    {
        EnsureStatus(PaymentStatus.Settled);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new DomainException("Refund reason is required (max 500 characters).");
        Status = PaymentStatus.Refunded;
        RefundReason = reason;
        RefundedAt = now;
        Post(LedgerEntryType.Refund, DriverAccount, RiderAccount, now);          // 6.11 Bút toán đảo: ghi thêm, không sửa dòng cũ
    }

    public bool IsLedgerBalanced() =>                                            // 6.12 Tổng Nợ == Tổng Có
        _ledgerEntries.Where(e => e.Direction == EntryDirection.Debit).Sum(e => e.Amount.Amount) ==
        _ledgerEntries.Where(e => e.Direction == EntryDirection.Credit).Sum(e => e.Amount.Amount);

    private void Post(LedgerEntryType type, string debitAccount, string creditAccount, DateTimeOffset now)
    {
        _ledgerEntries.Add(LedgerEntry.Create(Id, debitAccount, EntryDirection.Debit, type, Amount.Copy(), now));
        _ledgerEntries.Add(LedgerEntry.Create(Id, creditAccount, EntryDirection.Credit, type, Amount.Copy(), now));
        if (!IsLedgerBalanced())                                                 // 6.13 Bất biến: luôn cân bằng
            throw new DomainException("Ledger entries must balance.");
    }

    private void EnsureStatus(PaymentStatus expected)
    {
        if (Status != expected)
            throw new InvalidPaymentStateException($"Payment must be {expected} but is {Status}.");
    }
}