using Microsoft.Extensions.Logging;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments.Dtos;
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
        // 1. Tìm payment; không có -> 404
        var payment = await reader.GetByIdAsync(paymentId, ct) ?? throw new PaymentNotFoundException(paymentId);

        // 2. Idempotent: đã hoàn rồi thì trả lại kết quả cũ, không hoàn lần 2
        if (payment.Status == PaymentStatus.Refunded)
            return new PaymentResult(PaymentMapper.ToDto(payment), IsReplay: true);

        // 3. Domain kiểm tra: chỉ Settled mới hoàn (sai -> 409), tạo bút toán đảo TRONG BỘ NHỚ
        //    (chưa SaveChanges nên nếu cổng lỗi, DbContext của request bị bỏ và DB không đổi)
        payment.Refund(reason, clock.GetUtcNow());

        // 4. Gọi cổng hoàn tiền (có timeout); key "refund-{id}" để cổng tự chống trùng
        var gateway = gateways.Create(payment.Method);
        var result = await callPolicy.RunAsync(token => gateway.RefundAsync(
            new GatewayRefundRequest($"refund-{payment.Id:D}", payment.GatewayReference!, payment.Amount), token), ct);

        if (result.Outcome == GatewayOutcome.TransientFailure)
            throw new PaymentGatewayUnavailableException("Payment gateway is temporarily unavailable.");
        if (result.Outcome == GatewayOutcome.Declined)
            throw new InvalidPaymentStateException("The payment gateway refused the refund.");

        // 5. Lưu trạng thái Refunded + 2 dòng sổ cái đảo trong 1 transaction (xmin chặn hoàn trùng song song)
        await unitOfWork.SaveChangesAsync(ct);
        logger.LogInformation("Payment {PaymentId} refunded", payment.Id);
        return new PaymentResult(PaymentMapper.ToDto(payment), IsReplay: false);
    }
}