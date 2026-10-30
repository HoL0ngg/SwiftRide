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
