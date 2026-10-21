namespace SwiftRide.PaymentService.Application.Payments.Dtos;

public sealed record LedgerEntryDto(Guid Id, string Account, string Direction, string Type,
    decimal Amount, string Currency, DateTimeOffset CreatedAt);

public sealed record PaymentDto(Guid Id, Guid TripId, Guid RiderId, Guid DriverId, decimal Amount, string Currency,
    string Method, string Status, string? FailureReason, string? RefundReason, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, DateTimeOffset? RefundedAt, IReadOnlyList<LedgerEntryDto> LedgerEntries);
// 13.1 DTO phẳng, không lộ entity Payment ra API (AGENTS: never expose persistence entities)