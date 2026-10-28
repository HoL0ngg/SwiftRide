using SwiftRide.PaymentService.Domain.Enums;
namespace SwiftRide.PaymentService.Application.Payments.Dtos;

public sealed record ProcessPaymentInput(Guid TripId, string? RiderId, string? DriverId, decimal Amount,
    string? Currency, PaymentMethod Method, string? PaymentToken, string? IdempotencyKey);  // Đầu vào use case, độc lập với HTTP
