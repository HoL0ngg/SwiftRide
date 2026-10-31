using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

public sealed class PaymentFailedConsumer : IConsumer<PaymentFailedV1>
{
    private readonly ITripReader _tripReader;
    private readonly ITripWriter _tripWriter;
    private readonly ITripUnitOfWork _unitOfWork;
    private readonly ITripWorkStore _workStore;
    private readonly TripDbContext _db;
    private readonly ILogger<PaymentFailedConsumer> _logger;

    public PaymentFailedConsumer(
        ITripReader tripReader,
        ITripWriter tripWriter,
        ITripUnitOfWork unitOfWork,
        ITripWorkStore workStore,
        TripDbContext db,
        ILogger<PaymentFailedConsumer> logger)
    {
        _tripReader = tripReader;
        _tripWriter = tripWriter;
        _unitOfWork = unitOfWork;
        _workStore = workStore;
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentFailedV1> context)
    {
        var message = context.Message;
        var consumerName = typeof(PaymentFailedConsumer).FullName ?? nameof(PaymentFailedConsumer);

        // 1. Check Inbox: deduplicate exact MessageId
        var inboxEntry = await _db.IntegrationInbox
            .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId, context.CancellationToken);

        if (inboxEntry?.ProcessedAtUtc != null)
        {
            _logger.LogInformation("PaymentFailedV1 {EventId} for trip {TripId} already processed. Skipping duplicate.", message.EventId, message.TripId);
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

        // 2. Load Trip
        var ctx = await _tripReader.LoadAsync(message.TripId, context.CancellationToken);
        if (ctx is null)
        {
            _logger.LogWarning("PaymentFailedV1 received for unknown trip {TripId}", message.TripId);
            inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
            inboxEntry.LastError = "TripNotFound";
            AttachInboxIfDetached(inboxEntry);
            await _db.SaveChangesAsync(context.CancellationToken);
            return;
        }

        if (ctx.Trip.Status == TripStatus.Paid)
        {
            _logger.LogWarning("Trip {TripId} was already settled to Paid. Late PaymentFailedV1 will not override terminal Paid state.", message.TripId);
            inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
            AttachInboxIfDetached(inboxEntry);
            await _unitOfWork.SaveAsync(context.CancellationToken);
            return;
        }

        if (ctx.Trip.Status == TripStatus.PaymentPending)
        {
            if (message.IsPermanent)
            {
                ctx.Trip.RecordPaymentFailure(PaymentFailureKind.Permanent);
                _tripWriter.Update(ctx);
                _workStore.StageUnownedRequiresAction(message.TripId, message.FailureCode);
                _logger.LogWarning("Permanent payment failure for trip {TripId}. Staged RequiresAction, trip remains in PaymentPending.", message.TripId);
            }
            else
            {
                ctx.Trip.RecordPaymentFailure(PaymentFailureKind.Transient);
                _tripWriter.Update(ctx);
                _workStore.StageUnownedRetry(message.TripId, DateTimeOffset.UtcNow.AddMinutes(1), message.FailureCode);
                _logger.LogInformation("Transient payment failure for trip {TripId}. Scheduled retry.", message.TripId);
            }
        }
        else
        {
            _logger.LogWarning("Trip {TripId} in state {Status} received PaymentFailedV1.", message.TripId, ctx.Trip.Status);
        }

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
