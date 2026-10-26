using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Entities;
namespace SwiftRide.PaymentService.Application.Payments;

internal static class PaymentMapper
{
    public static PaymentDto ToDto(Payment p) => new(                                   // 15.1 Domain -> DTO
        p.Id, p.TripId, p.RiderId, p.DriverId, p.Amount.Amount, p.Amount.Currency,
        p.Method.ToString(), p.Status.ToString(), p.FailureReason, p.RefundReason,
        p.CreatedAt, p.CompletedAt, p.RefundedAt,
        p.LedgerEntries.OrderBy(e => e.CreatedAt).ThenBy(e => e.Direction)
            .Select(e => new LedgerEntryDto(e.Id, e.Account, e.Direction.ToString(), e.Type.ToString(),
                e.Amount.Amount, e.Amount.Currency, e.CreatedAt)).ToList());
}