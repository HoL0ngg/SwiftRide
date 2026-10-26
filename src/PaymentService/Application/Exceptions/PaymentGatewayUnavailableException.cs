namespace SwiftRide.PaymentService.Application.Exceptions;

public sealed class PaymentGatewayUnavailableException(string message) : Exception(message);
// -> 503 (Trip được phép retry)