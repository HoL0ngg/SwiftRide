namespace SwiftRide.PaymentService.Application.Abstractions;

public sealed record PaymentGatewaySettings(TimeSpan Timeout);  // 11.1 Timeout đọc từ cấu hình, không hard-code