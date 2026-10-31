using Microsoft.Extensions.Logging;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Domain.Repositories;
using SwiftRide.PaymentService.Domain.ValueObjects;

namespace SwiftRide.PaymentService.Application.Payments.UseCases;

public sealed class ProcessPaymentUseCase(
    IPaymentReader reader, IPaymentWriter writer, IUnitOfWork unitOfWork,
    IPaymentGatewayFactory gateways, GatewayCallPolicy callPolicy,
    TimeProvider clock, ILogger<ProcessPaymentUseCase> logger,
    IPaymentProcessingStore? processingStore = null,
    IPaymentMetrics? metrics = null)
{
    private readonly IPaymentMetrics _metrics = metrics ?? NullPaymentMetrics.Instance;

    // Phải lớn hơn timeout cổng (mặc định 10s). Pending quá hạn này mới được xử lý tiếp (dành cho fallback khi không có processingStore).
    private static readonly TimeSpan PendingLease = TimeSpan.FromSeconds(30);

    public async Task<PaymentResult> ExecuteAsync(ProcessPaymentInput input, CancellationToken ct)
    {
        // 1. Dựng Payment (Pending) trong bộ nhớ, validate nghiệp vụ trước khi đụng DB/cổng
        var amount = Money.Create(input.Amount, input.Currency);
        var candidate = Payment.Create(input.TripId, input.RiderId, input.DriverId, amount,
            input.Method, input.IdempotencyKey, input.PaymentToken, clock.GetUtcNow());

        // 2. Key đã có?
        var existing = await reader.GetByIdempotencyKeyAsync(candidate.IdempotencyKey, ct);
        Payment payment;
        var now = clock.GetUtcNow();

        if (existing is null)
        {
            // 3. INSERT Pending TRƯỚC khi gọi cổng: unique key chọn ra đúng 1 người thắng
            writer.Add(candidate);
            if (processingStore is not null)
            {
                await processingStore.InsertAsync(candidate.Id, candidate.RequestFingerprint, candidate.DriverId!, now, ct);
            }

            try { await unitOfWork.SaveChangesAsync(ct); }
            catch (DuplicateIdempotencyKeyException)
            {
                var winner = await reader.GetByIdempotencyKeyAsync(candidate.IdempotencyKey, ct);
                if (winner is null) throw;
                return await ReplayAsync(winner, candidate, ct);
            }
            payment = candidate;
        }
        else
        {
            if (!existing.HasSameRequestAs(candidate))
                throw new IdempotencyConflictException();

            if (existing.Status != PaymentStatus.Pending)
            {
                return await ReplayAsync(existing, candidate, ct);
            }

            if (processingStore is null)
            {
                var stale = clock.GetUtcNow() - existing.UpdatedAt >= PendingLease;
                if (!stale) return await ReplayAsync(existing, candidate, ct);
                existing.AdoptDriverFrom(candidate);
                payment = existing;
            }
            else
            {
                var proc = await processingStore.GetAsync(existing.Id, ct);
                if (proc is null)
                {
                    // Legacy record thiếu metadata v2: từ chối mutate để bảo vệ tính nhất quán dữ liệu
                    throw new IdempotencyConflictException("Legacy pending payment cannot be mutated.");
                }

                existing.AdoptDriverFrom(candidate);
                payment = existing;
            }
        }

        // 4. Zero fare tự động settled và ghi 2 dòng sổ cái 0 VND mà không gọi cổng
        if (payment.Amount.Amount == 0m)
        {
            var zeroNow = clock.GetUtcNow();
            payment.MarkSettled("zero-fare", zeroNow);
            if (processingStore is not null)
            {
                await processingStore.CompleteAsync(payment.Id, Guid.Empty, "settled", ct);
            }
            await unitOfWork.SaveChangesAsync(ct);
            _metrics.PaymentSettled();

            logger.LogInformation("Zero-fare payment {PaymentId} for trip {TripId} auto-settled without gateway call",
                payment.Id, payment.TripId);
            return new PaymentResult(PaymentMapper.ToDto(payment), IsReplay: false);
        }

        // 5. Claim nguyên tử trước khi gọi cổng: cưỡng chế lease 60s và tối đa 3 gateway attempts
        var ownerId = Guid.NewGuid();
        PaymentProcessingDto? processingDto = null;
        if (processingStore is not null)
        {
            var claim = await processingStore.TryClaimAsync(payment.Id, ownerId, now, TimeSpan.FromSeconds(60), maxAttempts: 3, ct);
            switch (claim.Status)
            {
                case ClaimStatus.AlreadyCompleted:
                    var winner = await reader.GetByIdempotencyKeyAsync(payment.IdempotencyKey, ct);
                    return await ReplayAsync(winner ?? payment, candidate, ct);

                case ClaimStatus.CurrentlyLeased:
                case ClaimStatus.MaxAttemptsExceeded:
                    processingDto = new PaymentProcessingDto(claim.AttemptsStarted, claim.LeaseUntil, claim.CanRetry, claim.RequiresAction);
                    return new PaymentResult(PaymentMapper.ToDto(payment, processingDto, claim.DriverId), IsReplay: existing is not null);

                case ClaimStatus.Claimed:
                    processingDto = new PaymentProcessingDto(claim.AttemptsStarted, claim.LeaseUntil, claim.CanRetry, claim.RequiresAction);
                    break;
            }
        }

        // 6. Chỉ người thắng claim mới tới đây: gọi cổng (có timeout, cổng chống trùng theo key)
        var gateway = gateways.Create(payment.Method);
        var result = await callPolicy.RunAsync(token => gateway.ChargeAsync(
            new GatewayChargeRequest(payment.IdempotencyKey, payment.Amount, input.PaymentToken!), token), ct);

        // 7. Lỗi tạm: để nguyên Pending trong DB, Trip retry cùng key
        if (result.Outcome == GatewayOutcome.TransientFailure)
        {
            throw new PaymentGatewayUnavailableException("Payment gateway is temporarily unavailable.");
        }

        // 8. Chốt status + 2 dòng ledger + fence owner của processing trong 1 transaction
        await unitOfWork.ExecuteInTransactionAsync(async txCt =>
        {
            var finalizeNow = clock.GetUtcNow();
            if (result.Outcome == GatewayOutcome.Succeeded)
            {
                payment.MarkSettled(result.Reference!, finalizeNow);
            }
            else
            {
                payment.MarkFailed(result.FailureCode ?? "declined", finalizeNow);
            }

            if (processingStore is not null)
            {
                var outcomeStr = payment.Status == PaymentStatus.Settled ? "settled" : "failed";
                var completed = await processingStore.CompleteAsync(payment.Id, ownerId, outcomeStr, txCt);
                if (!completed)
                {
                    throw new IdempotencyConflictException("Fencing conflict: another worker claimed or finalized this payment.");
                }
            }

            await unitOfWork.SaveChangesAsync(txCt);
        }, ct);

        if (payment.Status == PaymentStatus.Settled)
        {
            _metrics.PaymentSettled();
        }
        else if (payment.Status == PaymentStatus.Failed)
        {
            _metrics.PaymentFailed();
        }

        logger.LogInformation("Payment {PaymentId} for trip {TripId} finished with status {Status}",
            payment.Id, payment.TripId, payment.Status);
        return new PaymentResult(PaymentMapper.ToDto(payment, processingDto), IsReplay: false);
    }

    private async Task<PaymentResult> ReplayAsync(Payment existing, Payment candidate, CancellationToken ct)
    {
        if (!existing.HasSameRequestAs(candidate))
            throw new IdempotencyConflictException();

        PaymentProcessingDto? proc = null;
        string? fallbackDriver = null;
        if (processingStore is not null)
        {
            proc = await processingStore.GetAsync(existing.Id, ct);
            fallbackDriver = await processingStore.GetDriverIdAsync(existing.Id, ct);
        }

        logger.LogInformation("Replayed payment {PaymentId} with status {Status}", existing.Id, existing.Status);
        return new PaymentResult(PaymentMapper.ToDto(existing, proc, fallbackDriver), IsReplay: true);
    }
}
