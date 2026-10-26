namespace SwiftRide.PaymentService.Application.Exceptions;

public sealed class PaymentNotFoundException(Guid id) : Exception($"Payment '{id}' was not found.");//404