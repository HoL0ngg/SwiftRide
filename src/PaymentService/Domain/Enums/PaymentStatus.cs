namespace SwiftRide.PaymentService.Domain.Enums;

// Khớp CHECK của DB: payments.status / refunds.status IN ('pending','settled','failed')
// Pending chỉ sống trong bộ nhớ (trước khi gọi cổng). Hoàn tiền KHÔNG phải là một status của payment:
// nó là một dòng riêng trong bảng refunds.
public enum PaymentStatus { Pending = 0, Settled = 1, Failed = 2 }

public enum RefundStatus { Pending = 0, Settled = 1, Failed = 2 }
