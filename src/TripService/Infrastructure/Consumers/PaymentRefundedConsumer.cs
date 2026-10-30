using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

public sealed class PaymentRefundedConsumer : IConsumer<PaymentRefundedV1>
{
    private readonly ITripWorkStore _workStore;
    private readonly ITripUnitOfWork _unitOfWork;
    private readonly IActiveAssignmentStore _assignmentStore;
    private readonly TripDbContext _db;
    private readonly ILogger<PaymentRefundedConsumer> _logger;

    public PaymentRefundedConsumer(
        ITripWorkStore workStore,
        ITripUnitOfWork unitOfWork,
        IActiveAssignmentStore assignmentStore,
        TripDbContext db,
        ILogger<PaymentRefundedConsumer> logger)
    {
        _workStore = workStore;
        _unitOfWork = unitOfWork;
        _assignmentStore = assignmentStore;
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentRefundedV1> context)
    {
        var message = context.Message;
        var consumerName = typeof(PaymentRefundedConsumer).FullName ?? nameof(PaymentRefundedConsumer);

        // 1. Inbox deduplication
        var inboxEntry = await _db.IntegrationInbox
            .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId, context.CancellationToken);

        if (inboxEntry?.ProcessedAtUtc != null)
        {
            _logger.LogInformation("PaymentRefundedV1 {EventId} for trip {TripId} already processed. Skipping duplicate.", message.EventId, message.TripId);
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

        _assignmentStore.ReleaseRider(message.TripId);
        _workStore.StageUnownedCompletion(message.TripId);
        var job = await _db.TripPaymentJobs.FirstOrDefaultAsync(j => j.TripId == message.TripId, context.CancellationToken);
        if (job != null)
        {
            job.Status = "Refunded";
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation("Compensation refunded successfully for trip {TripId}, refund {RefundId}, amount {Amount} {Currency}. Rider released.",
            message.TripId, message.RefundId, message.Amount, message.Currency);

        inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
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
