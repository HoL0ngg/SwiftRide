namespace SwiftRide.PaymentService.Application.Exceptions;

public sealed class DuplicateIdempotencyKeyException() : Exception("A payment with the same idempotency key already exists.");
// nội bộ: Infrastructure ném, use case bắt