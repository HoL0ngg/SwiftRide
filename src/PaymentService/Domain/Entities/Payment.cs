using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Services;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Domain.Entities;

// Aggregate root. Một Payment + LedgerEntries + (tối đa 1) Refund luôn được lưu cùng transaction.
// Payment chỉ được INSERT một lần ở trạng thái cuối (Settled/Failed), không UPDATE về sau.
public sealed class Payment
{
    private const string RiderPrefix = "rider:";
    private const string DriverPrefix = "driver:";

    private readonly List<LedgerEntry> _ledgerEntries = [];
    private Refund? _refund;
    private string? _driverId;   // Bảng payments KHÔNG có cột driver_id => chỉ nhớ lúc tạo, sau đó suy ra từ sổ cái

    public Guid Id { get; private set; }
    public Guid TripId { get; private set; }
    public string RiderId { get; private set; } = default!;        // DB: rider_id text
    public Money Amount { get; private set; } = default!;
    public PaymentMethod Method { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string IdempotencyKey { get; private set; } = default!;
    public string RequestFingerprint { get; private set; } = default!;
    public string? GatewayReference { get; private set; }
    public string? FailureCode { get; private set; }               // DB: failure_code (mã lỗi, không phải câu chữ dài)
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }
    public IReadOnlyCollection<LedgerEntry> LedgerEntries => _ledgerEntries;
    public Refund? Refund => _refund;

    // Payment đã Settled: lấy từ dòng Có của sổ cái. Payment Failed (không có sổ cái): null.
    public string? DriverId => _driverId ?? DriverFromLedger();

    private string RiderAccount => $"{RiderPrefix}{RiderId}";
    private string DriverAccount => $"{DriverPrefix}{DriverId ?? throw new InvalidOperationException("Driver is unknown.")}";

    private Payment() { }

    public static Payment Create(Guid tripId, string? riderId, string? driverId, Money amount,
        PaymentMethod method, string? idempotencyKey, DateTimeOffset now)
    {
        if (tripId == Guid.Empty)
            throw new DomainException("TripId is required.");
        var rider = NormalizeParty(riderId, "RiderId");
        var driver = NormalizeParty(driverId, "DriverId");
        if (string.Equals(rider, driver, StringComparison.Ordinal))
            throw new DomainException("Rider and driver must be different users.");
        if (!Enum.IsDefined(method))
            throw new DomainException("Unsupported payment method.");
        var key = PaymentIdempotency.EnsureValid(idempotencyKey, tripId);

        return new Payment
        {
            Id = Guid.CreateVersion7(),
            TripId = tripId,
            RiderId = rider,
            _driverId = driver,
            Amount = amount,
            Method = method,
            Status = PaymentStatus.Pending,
            IdempotencyKey = key,
            RequestFingerprint = PaymentIdempotency.Fingerprint(tripId, rider, driver, amount, method),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public bool HasSameRequestAs(Payment other) =>
        string.Equals(RequestFingerprint, other.RequestFingerprint, StringComparison.Ordinal);

    // Payment Pending đọc lại từ DB không có driverId (bảng không có cột này).
    // Cùng fingerprint => cùng driver, nên nhận lại từ request hiện tại để ghi ledger.
    public void AdoptDriverFrom(Payment sameRequest) => _driverId ??= sameRequest._driverId;

    // Pending -> Settled, ghi 2 dòng sổ cái: rider Nợ, driver Có
    public void MarkSettled(string gatewayReference, DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Pending);
        if (string.IsNullOrWhiteSpace(gatewayReference))
            throw new DomainException("Gateway reference is required.");
        Status = PaymentStatus.Settled;
        GatewayReference = gatewayReference;
        SettledAt = now;                  // DB: (status='settled') = (settled_at IS NOT NULL)
        UpdatedAt = now;
        PostCharge(now);
    }

    // Pending -> Failed (không ghi sổ cái, settled_at phải NULL)
    public void MarkFailed(string failureCode, DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Pending);
        if (string.IsNullOrWhiteSpace(failureCode))
            throw new DomainException("Failure code is required.");
        Status = PaymentStatus.Failed;
        FailureCode = failureCode.Length > 100 ? failureCode[..100] : failureCode;
        UpdatedAt = now;
    }

    // ---- Hoàn tiền: 2 bước giống thu tiền (tạo ứng viên -> gọi cổng -> chốt) ----

    // Bước 1 (trước khi gọi cổng): kiểm tra luật, tạo Refund Pending trong bộ nhớ
    public Refund RequestRefund(string? reason, DateTimeOffset now)
    {
        EnsureStatus(PaymentStatus.Settled);
        if (_refund is not null)
            throw new InvalidPaymentStateException("Payment already has a refund.");
        _refund = Refund.Create(Id, Amount, Refund.NormalizeReason(reason), now);   // hoàn toàn bộ số tiền
        return _refund;
    }

    // Bước 2 (cổng đã hoàn xong): chốt Refund + ghi bút toán đảo (Nợ tài xế, Có rider)
    public void SettleRefund(string gatewayReference, DateTimeOffset now)
    {
        var refund = _refund ?? throw new InvalidPaymentStateException("No refund was requested.");
        refund.MarkSettled(gatewayReference, DriverAccount, RiderAccount, now);
    }

    // Dùng khi refund đã tồn tại: cùng lý do => replay, khác => 409
    public bool IsSameRefundRequest(string? reason) =>
        _refund is not null && string.Equals(_refund.RequestFingerprint,
            PaymentIdempotency.RefundFingerprint(Id, Amount, Refund.NormalizeReason(reason)), StringComparison.Ordinal);

    public bool IsLedgerBalanced() =>
        _ledgerEntries.Where(e => e.Direction == EntryDirection.Debit).Sum(e => e.Amount.Amount) ==
        _ledgerEntries.Where(e => e.Direction == EntryDirection.Credit).Sum(e => e.Amount.Amount);

    private void PostCharge(DateTimeOffset now)
    {
        _ledgerEntries.Add(LedgerEntry.Create(Id, RiderAccount, EntryDirection.Debit, Amount.Copy(), now));
        _ledgerEntries.Add(LedgerEntry.Create(Id, DriverAccount, EntryDirection.Credit, Amount.Copy(), now));
        if (!IsLedgerBalanced())
            throw new DomainException("Ledger entries must balance.");
    }

    private string? DriverFromLedger()
    {
        var credit = _ledgerEntries.FirstOrDefault(e => e.Direction == EntryDirection.Credit);
        return credit is not null && credit.Account.StartsWith(DriverPrefix, StringComparison.Ordinal)
            ? credit.Account[DriverPrefix.Length..]
            : null;
    }

    private static string NormalizeParty(string? value, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 100)
            throw new DomainException($"{name} is required (max 100 characters).");
        return trimmed;
    }

    private void EnsureStatus(PaymentStatus expected)
    {
        if (Status != expected)
            throw new InvalidPaymentStateException($"Payment must be {expected} but is {Status}.");
    }
}
