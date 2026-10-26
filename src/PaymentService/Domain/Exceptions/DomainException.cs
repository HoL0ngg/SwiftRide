namespace SwiftRide.PaymentService.Domain.Exceptions;

public class DomainException(string message) : Exception(message);
// 2.1 Vi phạm quy tắc nghiệp vụ -> API trả 400