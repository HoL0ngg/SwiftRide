namespace SwiftRide.PaymentService.Application.Exceptions;

public sealed class ConcurrentPaymentUpdateException() : Exception("The payment was modified by another request. Retry.");  // -> 409