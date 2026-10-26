using Microsoft.Extensions.Logging;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Repositories;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Application.Payments.UseCases;

public sealed class ProcessPaymentUseCase(                    // 17.1 DIP: toàn bộ phụ thuộc là interface
    IPaymentReader reader, IPaymentWriter writer, IUnitOfWork unitOfWork,
    IPaymentGatewayFactory gateways, GatewayCallPolicy callPolicy,
    TimeProvider clock, ILogger<ProcessPaymentUseCase> logger)
{
    public async Task<PaymentResult> ExecuteAsync(ProcessPaymentInput input, CancellationToken ct)
    {
        // 1. Dựng Payment (Pending) trong bộ nhớ: mọi validate nghiệp vụ chạy ở đây, TRƯỚC khi gọi cổng
        var amount = Money.Create(input.Amount, input.Currency); //đối tượng tiền: Money.Create(85000, "VND")

        var candidate = Payment.Create(input.TripId, input.RiderId, input.DriverId, amount,
            input.Method, input.IdempotencyKey, clock.GetUtcNow()); //payment “ứng viên” (candidate) trạng thái Pending, chỉ nằm trong bộ nhớ

        // 2. Idempotency: key đã có -> trả kết quả cũ (cùng payload) hoặc 409 (khác payload)
        var existing = await reader.GetByIdempotencyKeyAsync(candidate.IdempotencyKey, ct);
        if (existing is not null) return Replay(existing, candidate);

        // 3. Token bắt buộc (kiểm tra sau bước 2 để request lặp không cần gửi lại token)
        if (string.IsNullOrWhiteSpace(input.PaymentToken))
            throw new DomainException("PaymentToken is required.");

        // 4. Chọn chiến lược theo phương thức (Factory) rồi gọi cổng, có timeout
        var gateway = gateways.Create(candidate.Method); //Chọn cổng thanh toán
        var result = await callPolicy.RunAsync(token => gateway.ChargeAsync(
            new GatewayChargeRequest(candidate.IdempotencyKey, candidate.Amount, input.PaymentToken), token), ct);

        //Áp kết quả cổng vào Domain
        var now = clock.GetUtcNow();
        switch (result.Outcome)
        {
            case GatewayOutcome.Succeeded:
                candidate.MarkSettled(result.Reference!, now);       // Ghi 2 dòng sổ cái cân bằng
                break;
            case GatewayOutcome.Declined:
                candidate.MarkFailed(result.FailureReason ?? "Payment was declined.", now); // Lỗi vĩnh viễn: lưu Failed
                break;
            default:
                // Lỗi tạm: KHÔNG lưu gì, trả 503 để Trip retry cùng idempotency key
                throw new PaymentGatewayUnavailableException("Payment gateway is temporarily unavailable.");
        }

        // 6. Lưu Payment + LedgerEntries trong 1 transaction local
        writer.Add(candidate);   // báo cho EF Core: "theo dõi Payment này (kèm LedgerEntries)"
        try { await unitOfWork.SaveChangesAsync(ct); } // INSERT xuống PostgreSQL
        catch (DuplicateIdempotencyKeyException) // 2 request song song: unique index chọn 1 người thắng
        {
            var winner = await reader.GetByIdempotencyKeyAsync(candidate.IdempotencyKey, ct);
            if (winner is null) throw;
            return Replay(winner, candidate);
        }

        logger.LogInformation("Payment {PaymentId} for trip {TripId} finished with status {Status}",
            candidate.Id, candidate.TripId, candidate.Status);       // 6.2 Chỉ log id/status, KHÔNG log token
        return new PaymentResult(PaymentMapper.ToDto(candidate), IsReplay: false);
    }

    private PaymentResult Replay(Payment existing, Payment candidate) //chạy khi key đã tồn tại
    {
        if (!existing.HasSameRequestAs(candidate))
            throw new IdempotencyConflictException();                // Cùng key, khác payload -> 409
        logger.LogInformation("Replayed payment {PaymentId} with status {Status}", existing.Id, existing.Status);
        return new PaymentResult(PaymentMapper.ToDto(existing), IsReplay: true);  // Cùng payload -> không thu tiền lần 2
    }
}