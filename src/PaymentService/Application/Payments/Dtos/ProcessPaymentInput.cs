using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Application.Payments.Dtos;

public sealed record ProcessPaymentInput(Guid TripId, Guid RiderId, Guid DriverId, decimal Amount,
    string? Currency, PaymentMethod Method, string? PaymentToken, string? IdempotencyKey);  // 12.1 Đầu vào use case, độc lập với HTTP