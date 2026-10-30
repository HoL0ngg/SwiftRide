using Microsoft.Extensions.Logging;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Repositories;

namespace SwiftRide.PaymentService.Application.Payments.UseCases;

public sealed class RefundPaymentUseCase(
    IPaymentReader reader,
    IUnitOfWork unitOfWork,
    IPaymentGatewayFactory gateways,
    GatewayCallPolicy callPolicy,
    TimeProvider clock,
    ILogger<RefundPaymentUseCase> logger,
    IRefundProcessingStore? processingStore = null,
    IPaymentMetrics? metrics = null)
{
    private readonly IPaymentMetrics _metrics = metrics ?? NullPaymentMetrics.Instance;

    public async Task<PaymentResult> ExecuteAsync(
        Guid paymentId,
        string? reason,
        CancellationToken ct,
        Guid? expectedTripId = null,
        string? expectedPaymentKey = null)
    {
        // 1. Tìm payment (kèm refund nếu có); không có -> 404
        var payment = await reader.GetByIdAsync(paymentId, ct) ?? throw new PaymentNotFoundException(paymentId);

        if (payment.Status != PaymentStatus.Settled)
        {
            throw new InvalidPaymentStateException($"Cannot refund payment in status {payment.Status}.");
        }

        // 2. Strict identity verification
        if (expectedTripId.HasValue && payment.TripId != expectedTripId.Value)
        {
            logger.LogWarning("Refund identity check failed: Payment {PaymentId} trip is {ActualTripId}, expected {ExpectedTripId}",
                paymentId, payment.TripId, expectedTripId.Value);
            throw new DomainException($"Payment {paymentId} does not belong to Trip {expectedTripId.Value}.");
        }

        if (!string.IsNullOrWhiteSpace(expectedPaymentKey) &&
            !string.Equals(payment.IdempotencyKey, expectedPaymentKey, StringComparison.Ordinal))
        {
            logger.LogWarning("Refund identity check failed: Payment {PaymentId} key is {ActualKey}, expected {ExpectedKey}",
                paymentId, payment.IdempotencyKey, expectedPaymentKey);
            throw new DomainException($"Payment {paymentId} idempotency key does not match {expectedPaymentKey}.");
        }

        // 3. Idempotent: đã có refund?
        if (payment.Refund is not null)
        {
            if (payment.Refund.Status == RefundStatus.Settled)
            {
                return Replay(payment, reason);
            }

            if (!payment.IsSameRefundRequest(reason))
            {
                throw new IdempotencyConflictException();
            }

            if (payment.Refund.Status == RefundStatus.Pending)
            {
                var (settled, latest) = await TryWaitForWinnerSettlementAsync(paymentId, reason, maxPolls: 6, ct);
                if (settled is not null)
                {
                    return settled;
                }
                payment = latest;
            }
            else if (payment.Refund.Status == RefundStatus.Failed)
            {
                payment.ResumeRefund(clock.GetUtcNow());
                try
                {
                    await unitOfWork.SaveChangesAsync(ct);
                }
                catch (DuplicateIdempotencyKeyException)
                {
                    return await WaitForWinnerSettlementAsync(paymentId, reason, ct);
                }
            }
        }
        else
        {
            // 4. Persist Pending TRƯỚC khi gọi cổng để đảm bảo tính bền vững intent
            payment.RequestRefund(reason, clock.GetUtcNow());
            if (processingStore is not null)
            {
                await processingStore.InsertAsync(
                    payment.Refund!.Id,
                    payment.Id,
                    payment.TripId,
                    payment.Refund.IdempotencyKey,
                    reason,
                    clock.GetUtcNow(),
                    ct);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (DuplicateIdempotencyKeyException)
            {
                return await WaitForWinnerSettlementAsync(paymentId, reason, ct);
            }
        }

        var activeRefund = payment.Refund!;

        // 5. Claim lease 60s và tối đa 3 gateway attempts qua refund_processing
        var ownerId = Guid.NewGuid();
        if (processingStore is not null)
        {
            var claim = await processingStore.TryClaimAsync(activeRefund.Id, ownerId, clock.GetUtcNow(), TimeSpan.FromSeconds(60), maxAttempts: 3, ct);
            switch (claim.Status)
            {
                case ClaimStatus.AlreadyCompleted:
                    var winner = await reader.GetByIdAsync(paymentId, ct);
                    if (winner?.Refund?.Status == RefundStatus.Settled)
                    {
                        return Replay(winner, reason);
                    }
                    return await WaitForWinnerSettlementAsync(paymentId, reason, ct);

                case ClaimStatus.CurrentlyLeased:
                    return new PaymentResult(PaymentMapper.ToDto(payment), IsReplay: true);

                case ClaimStatus.MaxAttemptsExceeded:
                    throw new InvalidPaymentStateException("Refund processing exceeded maximum retry attempts.");

                case ClaimStatus.Claimed:
                    break;
            }
        }

        // 6. Gọi cổng hoàn tiền (cổng chống trùng theo operation "refund" + key "payment-{id}-refund")
        var gateway = gateways.Create(payment.Method);
        var result = await callPolicy.RunAsync(token => gateway.RefundAsync(
            new GatewayRefundRequest(activeRefund.IdempotencyKey, payment.GatewayReference ?? payment.IdempotencyKey, payment.Amount), token), ct);

        if (result.Outcome == GatewayOutcome.TransientFailure)
        {
            // Giữ nguyên Pending trong DB để lần sau resume
            throw new PaymentGatewayUnavailableException("Payment gateway is temporarily unavailable.");
        }

        if (result.Outcome == GatewayOutcome.Declined)
        {
            try
            {
                await unitOfWork.ExecuteInTransactionAsync(async txCt =>
                {
                    payment.FailRefund(result.FailureCode ?? "declined", clock.GetUtcNow());
                    if (processingStore is not null)
                    {
                        var completed = await processingStore.CompleteAsync(activeRefund.Id, ownerId, "failed", txCt);
                        if (!completed)
                        {
                            throw new IdempotencyConflictException("Fencing conflict: another worker claimed or finalized this refund.");
                        }
                    }
                    await unitOfWork.SaveChangesAsync(txCt);
                }, ct);
            }
            catch (Exception ex) when (ex is DuplicateIdempotencyKeyException || ex is IdempotencyConflictException)
            {
                return await WaitForWinnerSettlementAsync(paymentId, reason, ct);
            }

            throw new InvalidPaymentStateException("The payment gateway refused the refund.");
        }

        // 7. Chốt Refund + 2 dòng sổ cái đảo chiều (Nợ tài xế, Có rider) + processing trong cùng transaction
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async txCt =>
            {
                payment.SettleRefund(result.Reference!, clock.GetUtcNow());
                if (processingStore is not null)
                {
                    var completed = await processingStore.CompleteAsync(activeRefund.Id, ownerId, "settled", txCt);
                    if (!completed)
                    {
                        throw new IdempotencyConflictException("Fencing conflict: another worker claimed or finalized this refund.");
                    }
                }
                await unitOfWork.SaveChangesAsync(txCt);
            }, ct);

            _metrics.PaymentRefunded();
        }
        catch (Exception ex) when (ex is DuplicateIdempotencyKeyException || ex is IdempotencyConflictException)
        {
            return await WaitForWinnerSettlementAsync(paymentId, reason, ct);
        }

        logger.LogInformation("Payment {PaymentId} refunded successfully", payment.Id);
        return new PaymentResult(PaymentMapper.ToDto(payment), IsReplay: false);
    }

    private async Task<(PaymentResult? Result, Payment LatestPayment)> TryWaitForWinnerSettlementAsync(Guid paymentId, string? reason, int maxPolls, CancellationToken ct)
    {
        Payment? latest = null;
        for (var i = 0; i < maxPolls; i++)
        {
            await Task.Delay(50, ct);
            latest = await reader.GetByIdAsync(paymentId, ct);
            if (latest?.Refund?.Status == RefundStatus.Settled)
            {
                return (Replay(latest, reason), latest);
            }
        }

        latest ??= await reader.GetByIdAsync(paymentId, ct);
        return (null, latest ?? throw new PaymentNotFoundException(paymentId));
    }

    private async Task<PaymentResult> WaitForWinnerSettlementAsync(Guid paymentId, string? reason, CancellationToken ct)
    {
        var (settled, latest) = await TryWaitForWinnerSettlementAsync(paymentId, reason, maxPolls: 40, ct);
        if (settled is not null)
        {
            return settled;
        }

        if (latest.Refund is not null && latest.Refund.Status == RefundStatus.Settled)
        {
            return Replay(latest, reason);
        }

        throw new IdempotencyConflictException("Concurrent refund conflict occurred.");
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
