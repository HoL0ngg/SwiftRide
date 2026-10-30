using System.Diagnostics;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Messaging;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Consumers;

public sealed class PaymentSettledConsumer : IConsumer<PaymentSettledV1>
{
    private static readonly ActivitySource ConsumerActivitySource = new("SwiftRide.TripService.Consumer", "1.0.0");
    private readonly ITripReader _tripReader;
    private readonly ITripWriter _tripWriter;
    private readonly ITripUnitOfWork _unitOfWork;
    private readonly ITripWorkStore _workStore;
    private readonly IActiveAssignmentStore _assignmentStore;
    private readonly ITripEventWriter _eventWriter;
    private readonly IPaymentClient _paymentClient;
    private readonly ITripMetrics _metrics;
    private readonly TripDbContext _db;
    private readonly ILogger<PaymentSettledConsumer> _logger;

    public PaymentSettledConsumer(
        ITripReader tripReader,
        ITripWriter tripWriter,
        ITripUnitOfWork unitOfWork,
        ITripWorkStore workStore,
        IActiveAssignmentStore assignmentStore,
        ITripEventWriter eventWriter,
        IPaymentClient paymentClient,
        TripDbContext db,
        ILogger<PaymentSettledConsumer> logger,
        ITripMetrics? metrics = null)
    {
        _tripReader = tripReader;
        _tripWriter = tripWriter;
        _unitOfWork = unitOfWork;
        _workStore = workStore;
        _assignmentStore = assignmentStore;
        _eventWriter = eventWriter;
        _paymentClient = paymentClient;
        _db = db;
        _logger = logger;
        _metrics = metrics ?? NullTripMetrics.Instance;
    }

    public async Task Consume(ConsumeContext<PaymentSettledV1> context)
    {
        var message = context.Message;

        Activity? consumerActivity = null;
        if (context.Headers is not null &&
            context.Headers.TryGetHeader("traceparent", out var tpObj) && tpObj is string tpStr &&
            ActivityContext.TryParse(tpStr, null, out var parentCtx))
        {
            consumerActivity = ConsumerActivitySource.StartActivity(
                "Consume PaymentSettledV1",
                ActivityKind.Consumer,
                parentCtx);
        }

        using (consumerActivity)
        {
            var consumerName = typeof(PaymentSettledConsumer).FullName ?? nameof(PaymentSettledConsumer);

        // 1. Check Inbox: deduplicate exact MessageId
        var inboxEntry = await _db.IntegrationInbox
            .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId, context.CancellationToken);

        if (inboxEntry?.ProcessedAtUtc != null)
        {
            _logger.LogInformation("PaymentSettledV1 {EventId} for trip {TripId} already processed. Skipping duplicate.", message.EventId, message.TripId);
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
            _logger.LogWarning("PaymentSettledV1 received for unknown trip {TripId}", message.TripId);
            inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
            inboxEntry.LastError = "TripNotFound";
            AttachInboxIfDetached(inboxEntry);
            await _db.SaveChangesAsync(context.CancellationToken);
            return;
        }

        // 3. Guard against late settlement after compensation was requested or completed
        var job = await _db.TripPaymentJobs.FirstOrDefaultAsync(j => j.TripId == message.TripId, context.CancellationToken);
        if (job != null && (job.Status == "CompensationRequested" || job.Status == "Refunded"))
        {
            _logger.LogWarning("Trip {TripId} has technical compensation status '{Status}'. Late PaymentSettledV1 rejected; trip will not transition to Paid.",
                message.TripId, job.Status);
            inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
            AttachInboxIfDetached(inboxEntry);
            await _unitOfWork.SaveAsync(context.CancellationToken);
            return;
        }

        // 4. Idempotency: if already Paid, ensure rider released, complete job and mark inbox
        if (ctx.Trip.Status == TripStatus.Paid)
        {
            _logger.LogInformation("Trip {TripId} is already in Paid status.", message.TripId);
            _assignmentStore.ReleaseRider(message.TripId);
            _workStore.StageUnownedCompletion(message.TripId);
            inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
            AttachInboxIfDetached(inboxEntry);
            await _unitOfWork.SaveAsync(context.CancellationToken);
            return;
        }

        if (ctx.Trip.Status == TripStatus.PaymentPending)
        {
            // 5. Strict identity & actor verification (TripId, RiderId, DriverId)
            if (message.TripId != ctx.Trip.Id ||
                message.RiderId != ctx.Trip.RiderId ||
                string.IsNullOrWhiteSpace(message.DriverId) ||
                message.DriverId != ctx.Trip.AssignedDriverId)
            {
                _logger.LogWarning("PaymentSettledV1 does not match trip identity (Trip: {MsgTrip} vs {Trip}, Rider: {MsgRider} vs {Rider}, Driver: {MsgDriver} vs {Driver}). Staging RequiresAction.",
                    message.TripId, ctx.Trip.Id, message.RiderId, ctx.Trip.RiderId, message.DriverId, ctx.Trip.AssignedDriverId);
                _workStore.StageUnownedRequiresAction(message.TripId, "settlement_identity_mismatch");
                inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
                AttachInboxIfDetached(inboxEntry);
                await _unitOfWork.SaveAsync(context.CancellationToken);
                return;
            }

            // 6. Check for financial mismatch against agreed snapshot
            if (message.Amount != ctx.Trip.Fare?.Amount || message.Currency != ctx.Trip.Fare?.Currency)
            {
                _logger.LogWarning("PaymentSettledV1 amount/currency ({Amount} {Currency}) does not match agreed fare ({FareAmount} {FareCurrency}) for trip {TripId}. Verifying remote payment.",
                    message.Amount, message.Currency, ctx.Trip.Fare?.Amount, ctx.Trip.Fare?.Currency, message.TripId);

                var remote = await _paymentClient.GetByTripAsync(ctx.Trip.Id, context.CancellationToken);
                if (remote != null && remote.Status == RemotePaymentStatus.Settled &&
                    (remote.Amount != ctx.Trip.Fare?.Amount || remote.Currency != ctx.Trip.Fare?.Currency))
                {
                    _logger.LogWarning("Financial mismatch confirmed for trip {TripId}: remote settled {ActualAmount} {ActualCurrency}. Initiating compensation saga.",
                        ctx.Trip.Id, remote.Amount, remote.Currency);

                    _workStore.StageUnownedRequiresAction(ctx.Trip.Id, "financial_mismatch_compensation_requested");
                    if (job != null)
                    {
                        job.Status = "CompensationRequested";
                        job.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    _eventWriter.Add(new PaymentCompensationRequestedV1(
                        EventId: Guid.NewGuid(),
                        TripId: ctx.Trip.Id,
                        PaymentId: remote.Id,
                        PaymentKey: message.PaymentKey,
                        Amount: remote.Amount,
                        Currency: remote.Currency,
                        ReasonCode: "financial_mismatch",
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        CorrelationId: message.CorrelationId,
                        CausationId: message.EventId));

                    inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
                    AttachInboxIfDetached(inboxEntry);
                    await _unitOfWork.SaveAsync(context.CancellationToken);
                    return;
                }
            }

            // 7. Identity and fare match: transition to Paid, release rider, complete job
            var settlement = new PaymentSettlement(
                message.PaymentId,
                message.TripId,
                message.Amount,
                message.Currency,
                message.PaymentKey);

            ctx.Trip.SettlePayment(settlement);
            _assignmentStore.ReleaseRider(ctx.Trip.Id);
            _tripWriter.Update(ctx);
            _workStore.StageUnownedCompletion(message.TripId);
            if (job != null)
            {
                job.Status = "Completed";
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
            _metrics.TripCompleted();

            _logger.LogInformation("Trip {TripId} transitioned to Paid via event PaymentSettledV1 (PaymentId: {PaymentId})",
                message.TripId, message.PaymentId);
        }
        else
        {
            _logger.LogWarning("Trip {TripId} is in status {Status}, cannot settle to Paid.", message.TripId, ctx.Trip.Status);
        }

        inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
        AttachInboxIfDetached(inboxEntry);
        await _unitOfWork.SaveAsync(context.CancellationToken);
        }
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
