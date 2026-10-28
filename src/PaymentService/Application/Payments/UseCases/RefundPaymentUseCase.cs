using Microsoft.Extensions.Logging;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Repositories;

namespace SwiftRide.PaymentService.Application.Payments.UseCases;

public sealed class RefundPaymentUseCase(IPaymentReader reader, IUnitOfWork unitOfWork,
    IPaymentGatewayFactory gateways, GatewayCallPolicy callPolicy, TimeProvider clock,
    ILogger<RefundPaymentUseCase> logger)
{
    public async Task<PaymentResult> ExecuteAsync(Guid paymentId, string? reason, CancellationToken ct)
    {
        // 1. Tìm payment (kèm refund nếu có); không có -> 404
        var payment = await reader.GetByIdAsync(paymentId, ct) ?? throw new PaymentNotFoundException(paymentId);

        // 2. Idempotent: đã có refund -> trả lại kết quả cũ (cùng lý do) hoặc 409 (khác lý do)
        if (payment.Refund is not null) return Replay(payment, reason);

        // 3. Domain kiểm tra: chỉ Settled mới hoàn, lý do hợp lệ. Tạo Refund Pending TRONG BỘ NHỚ
        //    (chưa SaveChanges nên nếu cổng lỗi thì DB không đổi)
        var refund = payment.RequestRefund(reason, clock.GetUtcNow());

        // 4. Gọi cổng hoàn tiền (có timeout); key "payment-{id}-refund" để cổng tự chống trùng
        var gateway = gateways.Create(payment.Method);
        var result = await callPolicy.RunAsync(token => gateway.RefundAsync(
            new GatewayRefundRequest(refund.IdempotencyKey, payment.GatewayReference!, payment.Amount), token), ct);

        if (result.Outcome == GatewayOutcome.TransientFailure)
            throw new PaymentGatewayUnavailableException("Payment gateway is temporarily unavailable.");
        if (result.Outcome == GatewayOutcome.Declined)
            // Không lưu refund 'failed': bảng refunds UNIQUE(payment_id) sẽ chặn mọi lần hoàn lại sau đó
            throw new InvalidPaymentStateException("The payment gateway refused the refund.");

        // 5. Chốt Refund + 2 dòng sổ cái đảo, rồi lưu trong 1 transaction
        payment.SettleRefund(result.Reference!, clock.GetUtcNow());
        try { await unitOfWork.SaveChangesAsync(ct); }
        catch (DuplicateIdempotencyKeyException)   // 2 request hoàn song song: UNIQUE(payment_id) chọn 1 người thắng
        {
            var winner = await reader.GetByIdAsync(paymentId, ct);
            if (winner?.Refund is null) throw;
            return Replay(winner, reason);
        }

        logger.LogInformation("Payment {PaymentId} refunded", payment.Id);
        return new PaymentResult(PaymentMapper.ToDto(payment), IsReplay: false);
    }

    private PaymentResult Replay(Payment payment, string? reason)
    {
        if (payment.Refund!.Status != RefundStatus.Settled)
            throw new InvalidPaymentStateException($"Payment already has a refund in status {payment.Refund.Status}.");
        if (!payment.IsSameRefundRequest(reason))
            throw new IdempotencyConflictException();
        return new PaymentResult(PaymentMapper.ToDto(payment), IsReplay: true);
    }
}
