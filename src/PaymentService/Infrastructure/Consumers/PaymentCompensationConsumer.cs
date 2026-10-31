using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Messaging;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Application.Payments.UseCases;
using SwiftRide.PaymentService.Infrastructure.Persistence;

namespace SwiftRide.PaymentService.Infrastructure.Consumers;

public sealed class PaymentCompensationConsumer : IConsumer<PaymentCompensationRequestedV1>
{
    private readonly RefundPaymentUseCase _useCase;
    private readonly PaymentDbContext _dbContext;
    private readonly IPaymentEventWriter _eventWriter;
    private readonly ILogger<PaymentCompensationConsumer> _logger;

    public PaymentCompensationConsumer(
        RefundPaymentUseCase useCase,
        PaymentDbContext dbContext,
        IPaymentEventWriter eventWriter,
        ILogger<PaymentCompensationConsumer> logger)
    {
        _useCase = useCase;
        _dbContext = dbContext;
        _eventWriter = eventWriter;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentCompensationRequestedV1> context)
    {
        var message = context.Message;
        var consumerName = typeof(PaymentCompensationConsumer).FullName ?? nameof(PaymentCompensationConsumer);

        // 1. Check Inbox: deduplicate exact MessageId
        var inboxEntry = await _dbContext.IntegrationInbox
            .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId, context.CancellationToken);

        if (inboxEntry?.ProcessedAtUtc != null)
        {
            _logger.LogInformation("PaymentCompensationRequestedV1 {EventId} for payment {PaymentId} already processed. Skipping duplicate.", message.EventId, message.PaymentId);
            return;
        }

        if (inboxEntry == null)
        {
            inboxEntry = new PaymentInboxRecord
            {
                ConsumerName = consumerName,
                MessageId = message.EventId,
                ReceivedAtUtc = DateTimeOffset.UtcNow
            };
            _dbContext.IntegrationInbox.Add(inboxEntry);
            try
            {
                await _dbContext.SaveChangesAsync(context.CancellationToken);
            }
            catch (DbUpdateException)
            {
                inboxEntry = await _dbContext.IntegrationInbox
                    .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId, context.CancellationToken);
                if (inboxEntry?.ProcessedAtUtc != null) return;
            }
        }

        // 2. Call RefundPaymentUseCase with strict identity checks (TripId, PaymentKey)
        PaymentResult result;
        try
        {
            result = await _useCase.ExecuteAsync(
                message.PaymentId,
                message.ReasonCode,
                context.CancellationToken,
                expectedTripId: message.TripId,
                expectedPaymentKey: message.PaymentKey);
        }
        catch (PaymentGatewayUnavailableException ex)
        {
            _logger.LogWarning(ex, "Transient error during refund for payment {PaymentId}. Message will be retried.", message.PaymentId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Permanent failure during refund for payment {PaymentId}", message.PaymentId);
            inboxEntry!.LastError = ex.Message;

            _eventWriter.Add(new PaymentCompensationFailedV1(
                EventId: Guid.NewGuid(),
                RefundId: null,
                PaymentId: message.PaymentId,
                TripId: message.TripId,
                ReasonCode: message.ReasonCode,
                SafeErrorMessage: ex.Message,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: message.CorrelationId,
                CausationId: message.EventId));

            inboxEntry.ProcessedAtUtc = DateTimeOffset.UtcNow;
            AttachInboxIfDetached(inboxEntry);
            await _dbContext.SaveChangesAsync(context.CancellationToken);
            return;
        }

        // 3. Stage outbox event based on refund outcome
        var now = DateTimeOffset.UtcNow;
        if (result.Payment.Refund?.Status.Equals("settled", StringComparison.OrdinalIgnoreCase) == true)
        {
            _eventWriter.Add(new PaymentRefundedV1(
                EventId: Guid.NewGuid(),
                RefundId: result.Payment.Refund.Id,
                PaymentId: message.PaymentId,
                TripId: message.TripId,
                Amount: result.Payment.Refund.Amount,
                Currency: result.Payment.Currency,
                OccurredAtUtc: now,
                CorrelationId: message.CorrelationId,
                CausationId: message.EventId));
        }
        else if (result.Payment.Refund?.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            _eventWriter.Add(new PaymentCompensationFailedV1(
                EventId: Guid.NewGuid(),
                RefundId: result.Payment.Refund.Id,
                PaymentId: message.PaymentId,
                TripId: message.TripId,
                ReasonCode: message.ReasonCode,
                SafeErrorMessage: "The payment gateway refused the refund.",
                OccurredAtUtc: now,
                CorrelationId: message.CorrelationId,
                CausationId: message.EventId));
        }
        else if (result.Payment.Refund?.Status.Equals("pending", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new TransientPaymentPendingException($"Refund for payment {message.PaymentId} is currently pending or leased.");
        }

        inboxEntry!.ProcessedAtUtc = now;
        AttachInboxIfDetached(inboxEntry);
        await _dbContext.SaveChangesAsync(context.CancellationToken);
    }

    private void AttachInboxIfDetached(PaymentInboxRecord entry)
    {
        if (_dbContext.Entry(entry).State == EntityState.Detached)
        {
            _dbContext.IntegrationInbox.Attach(entry);
        }
        _dbContext.Entry(entry).Property(x => x.ProcessedAtUtc).IsModified = true;
    }
}
