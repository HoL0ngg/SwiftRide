using Microsoft.AspNetCore.Mvc;
using SwiftRide.PaymentService.Api.Requests;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Application.Payments.UseCases;

namespace SwiftRide.PaymentService.Api;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/payments").WithTags("Payments");   // gom mọi đường dẫn bắt đầu bằng /payments
        group.MapPost("", CreatePaymentAsync);                              // POST /payments  → chạy hàm CreatePaymentAsync
        group.MapGet("/{id:guid}", GetPaymentAsync);                        // GET  /payments/{id}
        group.MapPost("/{id:guid}/refund", RefundPaymentAsync);             // POST /payments/{id}/refund
        return endpoints;
    }

    private static async Task<IResult> CreatePaymentAsync(
        CreatePaymentRequest request, // đọc JSON body thành object CreatePaymentRequest
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,      // lấy từ header Idempotency-Key
        ProcessPaymentUseCase useCase, CancellationToken ct) //ASP.NET tự tạo ProcessPaymentUseCase, ct là tín hiệu hủy request.
    {
        var result = await useCase.ExecuteAsync(new ProcessPaymentInput(    //gói dữ liệu HTTP vào ProcessPaymentInput
            request.TripId, request.RiderId, request.DriverId, request.Amount,
            request.Currency ?? "VND", request.Method, request.PaymentToken, idempotencyKey), ct);

        return result.IsReplay
            ? Results.Ok(result.Payment)                                           // Lặp lại -> 200, kết quả cũ
            : Results.Created($"/payments/{result.Payment.Id}", result.Payment);   // Mới -> 201
    }

    private static async Task<IResult> GetPaymentAsync(Guid id, GetPaymentUseCase useCase, CancellationToken ct) =>
        Results.Ok(await useCase.ExecuteAsync(id, ct));                            // 32.1 Không có -> use case ném 404

    private static async Task<IResult> RefundPaymentAsync(Guid id, RefundPaymentRequest request,
        RefundPaymentUseCase useCase, CancellationToken ct) =>
        Results.Ok((await useCase.ExecuteAsync(id, request.Reason, ct)).Payment);  // 33.1 Hoàn xong trả payment (Status=Refunded)
}