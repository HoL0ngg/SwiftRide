namespace SwiftRide.PaymentService.Application.Payments.Dtos;

public sealed record LedgerEntryDto(string Account, string Direction, decimal Amount, string Currency, DateTimeOffset CreatedAt);

public sealed record RefundDto(Guid Id, decimal Amount, string Currency, string Reason, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? SettledAt, IReadOnlyList<LedgerEntryDto> LedgerEntries);

public sealed record PaymentDto(Guid Id, Guid TripId, string RiderId, string? DriverId, decimal Amount, string Currency,
    string Method, string Status, string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? SettledAt,
    IReadOnlyList<LedgerEntryDto> LedgerEntries, RefundDto? Refund);
// DTO phẳng, không lộ entity Payment ra API
