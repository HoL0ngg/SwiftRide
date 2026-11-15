using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

public sealed class PaymentCompensationFailedConsumer : IConsumer<PaymentCompensationFailedV1>
{
    private readonly ITripWorkStore _workStore;
    private readonly ITripUnitOfWork _unitOfWork;
    private readonly TripDbContext _db;
    private readonly ILogger<PaymentCompensationFailedConsumer> _logger;

    public PaymentCompensationFailedConsumer(
        ITripWorkStore workStore,
        ITripUnitOfWork unitOfWork,
        TripDbContext db,
        ILogger<PaymentCompensationFailedConsumer> logger)
    {
        _workStore = workStore;
        _unitOfWork = unitOfWork;
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentCompensationFailedV1> context)
    {
        var message = context.Message;
        using var messageScope = _logger.BeginScope(
            "EventType={EventType} EventId={EventId} TripId={TripId} CorrelationId={CorrelationId} CausationId={CausationId}",
            nameof(PaymentCompensationFailedV1), message.EventId, message.TripId, message.CorrelationId, message.CausationId);
        _logger.LogInformation(
            """
            ==================== RABBITMQ MESSAGE RECEIVED [RECEIVED] ====================
            [MESSAGE INFO]
            MessageType        : {EventType}
            EventId            : {EventId}
            TripId             : {TripId}

            [BUSINESS INFO]
            PaymentId          : {PaymentId}
            RefundId           : {RefundId}
            ReasonCode         : {ReasonCode}

            [CORRELATION INFO]
            CorrelationId      : {CorrelationId}
            CausationId        : {CausationId}

            [RETRY INFO]
            RetryAttempt       : {RetryAttempt}
            RedeliveryCount    : {RedeliveryCount}

            [MESSAGE CONTEXT]
            TransportMessageId : {TransportMessageId}
            Source             : {SourceAddress}
            Destination        : {DestinationAddress}
            InputAddress       : {InputAddress}

            [CONTEXT.MESSAGE]
            {ContextMessage}
            =============================================================================
            """,
            nameof(PaymentCompensationFailedV1),
            message.EventId,
            message.TripId,
            message.PaymentId,
            message.RefundId,
            message.ReasonCode,
            message.CorrelationId,
            message.CausationId,
            context.GetRetryAttempt(),
            context.GetRedeliveryCount(),
            context.MessageId,
            context.SourceAddress,
            context.DestinationAddress,
            context.ReceiveContext?.InputAddress,
            JsonSerializer.Serialize(new
            {
                message.EventId,
                message.TripId,
                message.PaymentId,
                message.RefundId,
                message.ReasonCode,
                message.OccurredAtUtc,
                message.CorrelationId,
                message.CausationId
            }, new JsonSerializerOptions { WriteIndented = true }));
        var consumerName = typeof(PaymentCompensationFailedConsumer).FullName ?? nameof(PaymentCompensationFailedConsumer);

        // 1. Inbox deduplication
        var inboxEntry = await _db.IntegrationInbox
            .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId, context.CancellationToken);

        if (inboxEntry?.ProcessedAtUtc != null)
        {
            _logger.LogInformation("PaymentCompensationFailedV1 {EventId} for trip {TripId} already processed. Skipping duplicate.", message.EventId, message.TripId);
            return;
        }

        if (inboxEntry == null)
        {
            inboxEntry = new IntegrationInboxRecord
            {
                ConsumerName = consumerName,
                MessageId = message.EventId,
                ReceivedAtUtc = DateTimeOffset.UtcNow
            };
            _db.IntegrationInbox.Add(inboxEntry);
            try
            {
                await _db.SaveChangesAsync(context.CancellationToken);
            }
            catch (DbUpdateException)
            {
                inboxEntry = await _db.IntegrationInbox
                    .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId, context.CancellationToken);
                if (inboxEntry?.ProcessedAtUtc != null) return;
            }
        }

        _workStore.StageUnownedRequiresAction(message.TripId, message.ReasonCode);
        var job = await _db.TripPaymentJobs.FirstOrDefaultAsync(j => j.TripId == message.TripId, context.CancellationToken);
        if (job != null)
        {
            job.Status = "CompensationFailed";
            job.RequiresAction = true;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        _logger.LogError("Compensation failed for trip {TripId} (PaymentId: {PaymentId}): {ErrorMessage}. Staged RequiresAction for manual intervention.",
            message.TripId, message.PaymentId, message.SafeErrorMessage);

        inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
        inboxEntry.LastError = message.SafeErrorMessage;
        AttachInboxIfDetached(inboxEntry);
        await _unitOfWork.SaveAsync(context.CancellationToken);
        _logger.LogInformation("[PROCESSED] {EventType} EventId={EventId} TripId={TripId}", nameof(PaymentCompensationFailedV1), message.EventId, message.TripId);
    }

    private void AttachInboxIfDetached(IntegrationInboxRecord entry)
    {
        if (_db.Entry(entry).State == EntityState.Detached)
        {
            _db.IntegrationInbox.Attach(entry);
        }
        _db.Entry(entry).Property(x => x.ProcessedAtUtc).IsModified = true;
    }
}
