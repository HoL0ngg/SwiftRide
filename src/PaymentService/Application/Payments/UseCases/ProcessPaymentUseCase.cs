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
    TimeProvider clock, ILogger<ProcessPaymentUseCase> logger)
{
    // Phải lớn hơn timeout cổng (mặc định 10s). Pending quá hạn này mới được xử lý tiếp.
    private static readonly TimeSpan PendingLease = TimeSpan.FromSeconds(30);

    public async Task<PaymentResult> ExecuteAsync(ProcessPaymentInput input, CancellationToken ct)
    {
        // 1. Dựng Payment (Pending) trong bộ nhớ, validate nghiệp vụ trước khi đụng DB/cổng
        var amount = Money.Create(input.Amount, input.Currency);
        var candidate = Payment.Create(input.TripId, input.RiderId, input.DriverId, amount,
            input.Method, input.IdempotencyKey, clock.GetUtcNow());

        // 2. Key đã có?
        var existing = await reader.GetByIdempotencyKeyAsync(candidate.IdempotencyKey, ct);
        Payment payment;

        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(input.PaymentToken))
                throw new DomainException("PaymentToken is required.");

            // 3. INSERT Pending TRƯỚC khi gọi cổng: unique key chọn ra đúng 1 người thắng
            writer.Add(candidate);
            try { await unitOfWork.SaveChangesAsync(ct); }
            catch (DuplicateIdempotencyKeyException)
            {
                var winner = await reader.GetByIdempotencyKeyAsync(candidate.IdempotencyKey, ct);
                if (winner is null) throw;
                return Replay(winner, candidate);   // người thua: Pending -> 202, đã xong -> kết quả cũ
            }
            payment = candidate;
        }
        else
        {
            var stale = existing.Status == PaymentStatus.Pending
                        && clock.GetUtcNow() - existing.UpdatedAt >= PendingLease;
            if (!stale) return Replay(existing, candidate);   // đang xử lý (202) hoặc đã có kết quả cuối

            // Pending quá hạn (lỗi tạm / process cũ chết): xử lý tiếp CÙNG payment, CÙNG key
            if (!existing.HasSameRequestAs(candidate)) throw new IdempotencyConflictException();
            if (string.IsNullOrWhiteSpace(input.PaymentToken))
                throw new DomainException("PaymentToken is required.");
            existing.AdoptDriverFrom(candidate);
            payment = existing;
        }

        // 4. Chỉ người thắng mới tới đây: gọi cổng (có timeout, cổng chống trùng theo key)
        var gateway = gateways.Create(payment.Method);
        var result = await callPolicy.RunAsync(token => gateway.ChargeAsync(
            new GatewayChargeRequest(payment.IdempotencyKey, payment.Amount, input.PaymentToken!), token), ct);

        // 5. Áp kết quả vào Domain
        var now = clock.GetUtcNow();
        switch (result.Outcome)
        {
            case GatewayOutcome.Succeeded: payment.MarkSettled(result.Reference!, now); break;
            case GatewayOutcome.Declined: payment.MarkFailed(result.FailureCode ?? "declined", now); break;
            default:
                // Lỗi tạm: để nguyên Pending trong DB, Trip retry cùng key (202 trong lease, xử lý tiếp sau lease)
                throw new PaymentGatewayUnavailableException("Payment gateway is temporarily unavailable.");
        }

        // 6. UPDATE status + INSERT 2 dòng ledger trong 1 transaction
        await unitOfWork.SaveChangesAsync(ct);

        logger.LogInformation("Payment {PaymentId} for trip {TripId} finished with status {Status}",
            payment.Id, payment.TripId, payment.Status);
        return new PaymentResult(PaymentMapper.ToDto(payment), IsReplay: false);
    }

    private PaymentResult Replay(Payment existing, Payment candidate)
    {
        if (!existing.HasSameRequestAs(candidate))
            throw new IdempotencyConflictException();
        logger.LogInformation("Replayed payment {PaymentId} with status {Status}", existing.Id, existing.Status);
        return new PaymentResult(PaymentMapper.ToDto(existing), IsReplay: true);
    }
}