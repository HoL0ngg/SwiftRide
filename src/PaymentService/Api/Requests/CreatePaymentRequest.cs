using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Api.Requests;

public sealed record CreatePaymentRequest(Guid TripId, string? RiderId, string? DriverId, decimal Amount,
    string? Currency, PaymentMethod Method, string? PaymentToken);   // Hợp đồng HTTP riêng, key đi bằng header
