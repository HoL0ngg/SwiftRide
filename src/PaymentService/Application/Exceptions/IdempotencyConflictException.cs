namespace SwiftRide.PaymentService.Application.Exceptions;

public sealed class IdempotencyConflictException() : Exception("The idempotency key was already used with a different request payload.");//409