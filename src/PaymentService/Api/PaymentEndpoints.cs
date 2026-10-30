using Microsoft.AspNetCore.Mvc;
using SwiftRide.PaymentService.Api.Observability;
using SwiftRide.PaymentService.Api.Requests;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Application.Payments.UseCases;
using SwiftRide.PaymentService.Domain.Enums;

namespace SwiftRide.PaymentService.Api;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/payments").WithTags("Payments");

        group.AddEndpointFilter(async (context, next) =>
        {
            var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expectedKey = config["Security:InternalApiKey"] ?? config["Payment:InternalApiKey"] ?? config["InternalApiKey"];
            if (string.IsNullOrWhiteSpace(expectedKey))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Internal API key is not configured.");
            }

            if (!context.HttpContext.Request.Headers.TryGetValue("X-Internal-Api-Key", out var headerKey) ||
                headerKey != expectedKey)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            return await next(context);
        });

        group.MapPost("", CreatePaymentAsync);                              // POST /payments
        group.MapGet("/{id:guid}", GetPaymentAsync);                        // GET  /payments/{id}
        group.MapPost("/{id:guid}/refund", RefundPaymentAsync);             // POST /payments/{id}/refund
        group.MapGet("", GetPaymentByTripAsync);                            // GET /payments?tripId=...
        return endpoints;
    }

    private static async Task<IResult> CreatePaymentAsync(
        CreatePaymentRequest request,                                       // JSON body
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,      // header Idempotency-Key
        ProcessPaymentUseCase useCase, CancellationToken ct)
    {
        var result = await useCase.ExecuteAsync(new ProcessPaymentInput(    // gói dữ liệu HTTP vào input của use case
            request.TripId, request.RiderId, request.DriverId, request.Amount,
            request.Currency ?? "VND", request.Method, request.PaymentToken, idempotencyKey), ct);

        if (result.Payment.Status == nameof(PaymentStatus.Pending))
            return Results.Accepted($"/payments/{result.Payment.Id}", result.Payment);   // đang xử lý -> 202

        return result.IsReplay
            ? Results.Ok(result.Payment)
            : Results.Created($"/payments/{result.Payment.Id}", result.Payment);
    }

    private static async Task<IResult> GetPaymentAsync(Guid id, GetPaymentUseCase useCase, CancellationToken ct) =>
        Results.Ok(await useCase.ExecuteAsync(id, ct));                            // Không có -> use case ném 404

    private static async Task<IResult> RefundPaymentAsync(Guid id, RefundPaymentRequest request,
        RefundPaymentUseCase useCase, CancellationToken ct)
    {
        var outcome = await useCase.ExecuteAsync(id, request.Reason, ct);
        return Results.Ok(outcome.Payment);
    }

    private static async Task<IResult> GetPaymentByTripAsync(
        [FromQuery] Guid tripId, GetPaymentByTripUseCase useCase, CancellationToken ct) =>
        Results.Ok(await useCase.ExecuteAsync(tripId, ct));
}
