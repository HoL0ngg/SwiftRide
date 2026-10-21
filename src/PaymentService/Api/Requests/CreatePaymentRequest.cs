using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Api.Requests;

public sealed record CreatePaymentRequest(Guid TripId, Guid RiderId, Guid DriverId, decimal Amount,
    string? Currency, PaymentMethod Method, string? PaymentToken);   // 29.1 Hợp đồng HTTP riêng, key đi bằng header