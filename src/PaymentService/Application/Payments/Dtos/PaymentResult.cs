namespace SwiftRide.PaymentService.Application.Payments.Dtos;

public sealed record PaymentResult(PaymentDto Payment, bool IsReplay);  // 14.1 IsReplay: Api quyết định 201 hay 200