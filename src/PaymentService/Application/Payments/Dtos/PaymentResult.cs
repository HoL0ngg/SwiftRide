namespace SwiftRide.PaymentService.Application.Payments.Dtos;

public sealed record PaymentResult(PaymentDto Payment, bool IsReplay);  // IsReplay: Api quyết định 201 hay 200
