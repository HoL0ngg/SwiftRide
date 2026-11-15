using System.Diagnostics;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Messaging;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Application.Payments.UseCases;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Infrastructure.Persistence;

namespace SwiftRide.PaymentService.Infrastructure.Consumers;

public sealed class RideCompletedConsumer : IConsumer<RideCompletedV1>
{
    private static readonly ActivitySource ConsumerActivitySource = new("SwiftRide.PaymentService.Consumer", "1.0.0");
    private readonly ProcessPaymentUseCase _useCase;
    private readonly PaymentDbContext _dbContext;
    private readonly IPaymentEventWriter _eventWriter;
    private readonly ILogger<RideCompletedConsumer> _logger;

    public RideCompletedConsumer(
        ProcessPaymentUseCase useCase,
        PaymentDbContext dbContext,
        IPaymentEventWriter eventWriter,
        ILogger<RideCompletedConsumer> logger)
    {
        _useCase = useCase;
        _dbContext = dbContext;
        _eventWriter = eventWriter;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<RideCompletedV1> context)
    {
        var message = context.Message;

        Activity? consumerActivity = null;
        if (context.Headers is not null &&
            context.Headers.TryGetHeader("traceparent", out var tpObj) && tpObj is string tpStr &&
            ActivityContext.TryParse(tpStr, null, out var parentCtx))
        {
            consumerActivity = ConsumerActivitySource.StartActivity(
                "Consume RideCompletedV1",
                ActivityKind.Consumer,
                parentCtx);
        }

        using (consumerActivity)
        {
            var consumerName = typeof(RideCompletedConsumer).FullName ?? nameof(RideCompletedConsumer);

            // Scope: moi log ben duoi deu mang theo cac field nay de de filter
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["EventId"] = message.EventId,
                ["TripId"] = message.TripId,
                ["CorrelationId"] = message.CorrelationId,
                ["TransportMessageId"] = context.MessageId,
                ["TraceId"] = consumerActivity?.TraceId.ToString()
            });

            // 0. Log ngay khi message duoc consumer nhan (truoc moi xu ly)
          
            _logger.LogInformation(
                """
                ==================== RABBITMQ MESSAGE RECEIVED ====================
                [MESSAGE INFO]
                MessageType       : {MessageType}
                EventId           : {EventId}
                TripId            : {TripId}
                RiderId           : {RiderId}
                DriverId          : {DriverId}

                [PAYMENT INFO]
                FareAmount        : {FareAmount}
                FareCurrency      : {FareCurrency}
                PaymentMethod     : {PaymentMethod}
                PaymentKey        : {PaymentKey}

                [CORRELATION INFO]
                CorrelationId     : {CorrelationId}

                [RETRY INFO]
                RetryAttempt      : {RetryAttempt}
                RedeliveryCount   : {RedeliveryCount}

                [MESSAGE CONTEXT]
                Source            : {SourceAddress}
                Destination       : {DestinationAddress}
                Queue             : {InputAddress}

                
                ====================================================================
                """,
                nameof(RideCompletedV1),
                message.EventId,
                message.TripId,
                message.RiderId,
                message.DriverId,
                message.FareAmount,
                message.FareCurrency,
                message.PaymentMethod,
                message.PaymentIdempotencyKey,
                message.CorrelationId,
                context.GetRetryAttempt(),
                context.GetRedeliveryCount(),
                context.SourceAddress,
                context.DestinationAddress,
                context.ReceiveContext.InputAddress
            );


            // 1. Check Inbox: deduplicate exact MessageId
            var inboxEntry = await _dbContext.IntegrationInbox
                .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId);

            if (inboxEntry?.ProcessedAtUtc != null)
            {
                _logger.LogInformation(
                    "[DUPLICATE] RideCompletedV1 {EventId} for trip {TripId} was already processed at {ProcessedAtUtc}. Skipping duplicate.",
                    message.EventId, message.TripId, inboxEntry.ProcessedAtUtc);
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
                    _logger.LogDebug("[INBOX] Created inbox entry for {EventId}.", message.EventId);
                }
                catch (DbUpdateException ex)
                {
                    _logger.LogWarning(ex,
                        "[INBOX] Concurrent insert detected for {EventId}. Reloading existing inbox entry.",
                        message.EventId);

                    inboxEntry = await _dbContext.IntegrationInbox
                        .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId);

                    if (inboxEntry?.ProcessedAtUtc != null)
                    {
                        _logger.LogInformation(
                            "[DUPLICATE] {EventId} was processed by a concurrent consumer. Skipping.",
                            message.EventId);
                        return;
                    }
                }
            }
            else
            {
                _logger.LogInformation(
                    "[INBOX] Existing unprocessed inbox entry found for {EventId} (previous error: {LastError}). Reprocessing.",
                    message.EventId, inboxEntry.LastError);
            }

            // 2. Call ProcessPaymentUseCase
            var method = Enum.TryParse<PaymentMethod>(message.PaymentMethod, true, out var parsedMethod)
                ? parsedMethod
                : PaymentMethod.Card;

            if (!string.Equals(method.ToString(), message.PaymentMethod, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[PAYMENT] Unknown PaymentMethod '{RawPaymentMethod}' for trip {TripId}. Falling back to {FallbackMethod}.",
                    message.PaymentMethod, message.TripId, method);
            }

            var input = new ProcessPaymentInput(
                TripId: message.TripId,
                RiderId: message.RiderId,
                DriverId: message.DriverId,
                Amount: message.FareAmount,
                Currency: message.FareCurrency,
                Method: method,
                PaymentToken: message.MockTokenAlias,
                IdempotencyKey: message.PaymentIdempotencyKey);

            _logger.LogInformation(
                "[PAYMENT] Executing ProcessPaymentUseCase for trip {TripId}: Amount={Amount} {Currency}, Method={Method}, PaymentKey={PaymentKey}.",
                message.TripId, message.FareAmount, message.FareCurrency, method, message.PaymentIdempotencyKey);

            PaymentResult result;
            try
            {
                result = await _useCase.ExecuteAsync(input, context.CancellationToken);
            }
            catch (PaymentGatewayUnavailableException ex)
            {
                _logger.LogWarning(ex,
                    "[TRANSIENT] Transient gateway error processing payment for trip {TripId}. Message will be retried (attempt {RetryAttempt}).",
                    message.TripId, context.GetRetryAttempt());
                throw; // Re-throw so MassTransit retry/schedule handles it
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "[TRANSIENT] Concurrency conflict processing payment for trip {TripId}. Retrying via broker (attempt {RetryAttempt}).",
                    message.TripId, context.GetRetryAttempt());
                throw;
            }
            catch (Npgsql.NpgsqlException ex)
            {
                _logger.LogWarning(ex,
                    "[TRANSIENT] Database exception processing payment for trip {TripId}. Retrying via broker (attempt {RetryAttempt}).",
                    message.TripId, context.GetRetryAttempt());
                throw;
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex,
                    "[TRANSIENT] Timeout processing payment for trip {TripId}. Retrying via broker (attempt {RetryAttempt}).",
                    message.TripId, context.GetRetryAttempt());
                throw;
            }
            catch (IdempotencyConflictException ex)
            {
                inboxEntry!.LastError = ex.Message;
                _logger.LogError(ex,
                    "[PERMANENT] Idempotency conflict for trip {TripId}, PaymentKey={PaymentKey}",
                    message.TripId, message.PaymentIdempotencyKey);

                var failedEvent = new PaymentFailedV1(
                    EventId: Guid.NewGuid(),
                    PaymentId: null,
                    TripId: message.TripId,
                    PaymentKey: message.PaymentIdempotencyKey,
                    FailureCode: "idempotency_conflict",
                    IsPermanent: true,
                    SafeErrorMessage: "Idempotency key was reused with conflicting payment parameters.",
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    CorrelationId: message.CorrelationId,
                    CausationId: message.EventId);

                _eventWriter.Add(failedEvent);

                _logger.LogWarning(
                    "[OUTBOX] Staged {EventType} {NewEventId} for trip {TripId} (FailureCode=idempotency_conflict, IsPermanent=true).",
                    nameof(PaymentFailedV1), failedEvent.EventId, message.TripId);

                inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
                AttachInboxIfDetached(inboxEntry);
                await _dbContext.SaveChangesAsync(context.CancellationToken);

                _logger.LogInformation("[DONE] {EventId} marked processed (idempotency conflict).", message.EventId);
                return;
            }
            catch (DomainException ex)
            {
                inboxEntry!.LastError = ex.Message;
                _logger.LogError(ex, "[PERMANENT] Domain error for trip {TripId}", message.TripId);

                var failedEvent = new PaymentFailedV1(
                    EventId: Guid.NewGuid(),
                    PaymentId: null,
                    TripId: message.TripId,
                    PaymentKey: message.PaymentIdempotencyKey,
                    FailureCode: "domain_error",
                    IsPermanent: true,
                    SafeErrorMessage: ex.Message,
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    CorrelationId: message.CorrelationId,
                    CausationId: message.EventId);

                _eventWriter.Add(failedEvent);

                _logger.LogWarning(
                    "[OUTBOX] Staged {EventType} {NewEventId} for trip {TripId} (FailureCode=domain_error, IsPermanent=true).",
                    nameof(PaymentFailedV1), failedEvent.EventId, message.TripId);

                inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
                AttachInboxIfDetached(inboxEntry);
                await _dbContext.SaveChangesAsync(context.CancellationToken);

                _logger.LogInformation("[DONE] {EventId} marked processed (domain error).", message.EventId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[TRANSIENT] Unexpected infrastructure/execution error processing payment for trip {TripId}. Message will be retried (attempt {RetryAttempt}).",
                    message.TripId, context.GetRetryAttempt());
                throw;
            }

            // 3. Stage outbox event based on status and mark inbox completed
            var now = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "[PAYMENT] Result for trip {TripId}: PaymentId={PaymentId}, Status={Status}, Amount={Amount} {Currency}, FailureCode={FailureCode}, " +
                "RequiresAction={RequiresAction}, CanRetry={CanRetry}.",
                message.TripId,
                result.Payment.Id,
                result.Payment.Status,
                result.Payment.Amount,
                result.Payment.Currency,
                result.Payment.FailureCode,
                result.Payment.Processing?.RequiresAction,
                result.Payment.Processing?.CanRetry);

            if (result.Payment.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            {
                if (result.Payment.Processing?.RequiresAction == true || result.Payment.Processing?.CanRetry == false)
                {
                    _logger.LogWarning(
                        "[PENDING] Payment {PaymentId} for trip {TripId} exceeded retry budget / requires action. Emitting PaymentFailedV1.",
                        result.Payment.Id, message.TripId);

                    var failedEvent = new PaymentFailedV1(
                        EventId: Guid.NewGuid(),
                        PaymentId: result.Payment.Id,
                        TripId: message.TripId,
                        PaymentKey: message.PaymentIdempotencyKey,
                        FailureCode: "max_attempts_exceeded",
                        IsPermanent: false,
                        SafeErrorMessage: "Payment retry attempts exhausted.",
                        OccurredAtUtc: now,
                        CorrelationId: message.CorrelationId,
                        CausationId: message.EventId);

                    _eventWriter.Add(failedEvent);

                    _logger.LogWarning(
                        "[OUTBOX] Staged {EventType} {NewEventId} for trip {TripId} (FailureCode=max_attempts_exceeded, IsPermanent=false).",
                        nameof(PaymentFailedV1), failedEvent.EventId, message.TripId);

                    inboxEntry!.ProcessedAtUtc = now;
                    AttachInboxIfDetached(inboxEntry);
                    await _dbContext.SaveChangesAsync(context.CancellationToken);

                    _logger.LogInformation("[DONE] {EventId} marked processed (max attempts exceeded).", message.EventId);
                    return;
                }

                // Still leased / transient pending: DO NOT mark inbox completed!
                _logger.LogInformation(
                    "[PENDING] Payment {PaymentId} for trip {TripId} is currently leased or pending. Retrying via broker (attempt {RetryAttempt}).",
                    result.Payment.Id, message.TripId, context.GetRetryAttempt());
                throw new TransientPaymentPendingException($"Payment for trip {message.TripId} is currently leased or pending.");
            }
            else if (result.Payment.Status.Equals("settled", StringComparison.OrdinalIgnoreCase))
            {
                var settledEvent = new PaymentSettledV1(
                    EventId: Guid.NewGuid(),
                    PaymentId: result.Payment.Id,
                    TripId: message.TripId,
                    RiderId: message.RiderId,
                    DriverId: message.DriverId,
                    Amount: result.Payment.Amount,
                    Currency: result.Payment.Currency,
                    PaymentKey: message.PaymentIdempotencyKey,
                    OccurredAtUtc: now,
                    CorrelationId: message.CorrelationId,
                    CausationId: message.EventId);

                _eventWriter.Add(settledEvent);

                _logger.LogInformation(
                    "[OUTBOX] Staged {EventType} {NewEventId} for trip {TripId}, PaymentId={PaymentId}, Amount={Amount} {Currency}.",
                    nameof(PaymentSettledV1), settledEvent.EventId, message.TripId,
                    result.Payment.Id, result.Payment.Amount, result.Payment.Currency);
            }
            else if (result.Payment.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                var failedEvent = new PaymentFailedV1(
                    EventId: Guid.NewGuid(),
                    PaymentId: result.Payment.Id,
                    TripId: message.TripId,
                    PaymentKey: message.PaymentIdempotencyKey,
                    FailureCode: result.Payment.FailureCode ?? "declined",
                    IsPermanent: true,
                    SafeErrorMessage: "The payment method was declined or failed.",
                    OccurredAtUtc: now,
                    CorrelationId: message.CorrelationId,
                    CausationId: message.EventId);

                _eventWriter.Add(failedEvent);

                _logger.LogWarning(
                    "[OUTBOX] Staged {EventType} {NewEventId} for trip {TripId}, PaymentId={PaymentId} (FailureCode={FailureCode}, IsPermanent=true).",
                    nameof(PaymentFailedV1), failedEvent.EventId, message.TripId,
                    result.Payment.Id, failedEvent.FailureCode);
            }
            else
            {
                _logger.LogWarning(
                    "[PAYMENT] Unexpected payment status '{Status}' for trip {TripId}. No outbox event staged.",
                    result.Payment.Status, message.TripId);
            }

            inboxEntry!.ProcessedAtUtc = now;
            AttachInboxIfDetached(inboxEntry);
            await _dbContext.SaveChangesAsync(context.CancellationToken);

            _logger.LogInformation(
                "[DONE] {EventId} for trip {TripId} processed successfully (PaymentStatus={Status}).",
                message.EventId, message.TripId, result.Payment.Status);
        }
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