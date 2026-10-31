namespace SwiftRide.PaymentService.Application.Exceptions;

public sealed class TransientPaymentPendingException(string message) : Exception(message);
