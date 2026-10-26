namespace SwiftRide.PaymentService.Domain.Exceptions;

public sealed class InvalidPaymentStateException(string message) : DomainException(message);
// 2.2 Sai trạng thái -> API trả 409