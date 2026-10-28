using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Entities;
namespace SwiftRide.PaymentService.Application.Payments;

internal static class PaymentMapper
{
    public static PaymentDto ToDto(Payment p) => new(
        p.Id, p.TripId, p.RiderId, p.DriverId, p.Amount.Amount, p.Amount.Currency,
        p.Method.ToString(), p.Status.ToString(), p.FailureCode, p.CreatedAt, p.SettledAt,
        p.LedgerEntries.OrderBy(e => e.Direction)
            .Select(e => new LedgerEntryDto(e.Account, e.Direction.ToString(), e.Amount.Amount, e.Amount.Currency, e.CreatedAt))
            .ToList(),
        p.Refund is null ? null : ToDto(p.Refund));

    private static RefundDto ToDto(Refund r) => new(
        r.Id, r.Amount.Amount, r.Amount.Currency, r.Reason, r.Status.ToString(), r.CreatedAt, r.SettledAt,
        r.LedgerEntries.OrderBy(e => e.Direction)
            .Select(e => new LedgerEntryDto(e.Account, e.Direction.ToString(), e.Amount.Amount, e.Amount.Currency, e.CreatedAt))
            .ToList());
}
