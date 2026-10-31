namespace SwiftRide.PaymentService.Application.Exceptions;

public sealed class IdempotencyConflictException(string? message = null)
    : Exception(message ?? "The idempotency key was already used with a different request payload."); // 409
