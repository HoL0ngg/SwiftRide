namespace SwiftRide.PaymentService.Domain.Enums;

public enum PaymentStatus { Pending = 0, Settled = 1, Failed = 2, Refunded = 3 }
// 1.1 Pending chỉ tồn tại trong bộ nhớ, không lưu DB