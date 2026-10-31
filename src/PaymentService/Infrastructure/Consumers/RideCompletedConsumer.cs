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

        // 1. Check Inbox: deduplicate exact MessageId
        var inboxEntry = await _dbContext.IntegrationInbox
            .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId);

        if (inboxEntry?.ProcessedAtUtc != null)
        {
            _logger.LogInformation("RideCompletedV1 {EventId} for trip {TripId} was already processed. Skipping duplicate.", message.EventId, message.TripId);
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
                    .FirstOrDefaultAsync(i => i.ConsumerName == consumerName && i.MessageId == message.EventId);
                if (inboxEntry?.ProcessedAtUtc != null) return;
            }
        }

        // 2. Call ProcessPaymentUseCase
        var method = Enum.TryParse<PaymentMethod>(message.PaymentMethod, true, out var parsedMethod)
            ? parsedMethod
            : PaymentMethod.Card;

        var input = new ProcessPaymentInput(
            TripId: message.TripId,
            RiderId: message.RiderId,
            DriverId: message.DriverId,
            Amount: message.FareAmount,
            Currency: message.FareCurrency,
            Method: method,
            PaymentToken: message.MockTokenAlias,
            IdempotencyKey: message.PaymentIdempotencyKey);

        PaymentResult result;
        try
        {
            result = await _useCase.ExecuteAsync(input, context.CancellationToken);
        }
        catch (PaymentGatewayUnavailableException ex)
        {
            _logger.LogWarning(ex, "Transient gateway error processing payment for trip {TripId}. Message will be retried.", message.TripId);
            throw; // Re-throw so MassTransit retry/schedule handles it
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrency conflict processing payment for trip {TripId}. Retrying via broker.", message.TripId);
            throw;
        }
        catch (Npgsql.NpgsqlException ex)
        {
            _logger.LogWarning(ex, "Database exception processing payment for trip {TripId}. Retrying via broker.", message.TripId);
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timeout processing payment for trip {TripId}. Retrying via broker.", message.TripId);
            throw;
        }
        catch (IdempotencyConflictException ex)
        {
            inboxEntry!.LastError = ex.Message;
            _logger.LogError(ex, "Permanent idempotency conflict for trip {TripId}", message.TripId);

            _eventWriter.Add(new PaymentFailedV1(
                EventId: Guid.NewGuid(),
                PaymentId: null,
                TripId: message.TripId,
                PaymentKey: message.PaymentIdempotencyKey,
                FailureCode: "idempotency_conflict",
                IsPermanent: true,
                SafeErrorMessage: "Idempotency key was reused with conflicting payment parameters.",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: message.CorrelationId,
                CausationId: message.EventId));

            inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
            AttachInboxIfDetached(inboxEntry);
            await _dbContext.SaveChangesAsync(context.CancellationToken);
            return;
        }
        catch (DomainException ex)
        {
            inboxEntry!.LastError = ex.Message;
            _logger.LogError(ex, "Permanent domain error for trip {TripId}", message.TripId);

            _eventWriter.Add(new PaymentFailedV1(
                EventId: Guid.NewGuid(),
                PaymentId: null,
                TripId: message.TripId,
                PaymentKey: message.PaymentIdempotencyKey,
                FailureCode: "domain_error",
                IsPermanent: true,
                SafeErrorMessage: ex.Message,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: message.CorrelationId,
                CausationId: message.EventId));

            inboxEntry!.ProcessedAtUtc = DateTimeOffset.UtcNow;
            AttachInboxIfDetached(inboxEntry);
            await _dbContext.SaveChangesAsync(context.CancellationToken);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected infrastructure/execution error processing payment for trip {TripId}. Message will be retried.", message.TripId);
            throw;
        }

        // 3. Stage outbox event based on status and mark inbox completed
        var now = DateTimeOffset.UtcNow;
        if (result.Payment.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            if (result.Payment.Processing?.RequiresAction == true || result.Payment.Processing?.CanRetry == false)
            {
                _logger.LogWarning("Payment for trip {TripId} exceeded retry budget / requires action. Emitting PaymentFailedV1.", message.TripId);
                _eventWriter.Add(new PaymentFailedV1(
                    EventId: Guid.NewGuid(),
                    PaymentId: result.Payment.Id,
                    TripId: message.TripId,
                    PaymentKey: message.PaymentIdempotencyKey,
                    FailureCode: "max_attempts_exceeded",
                    IsPermanent: false,
                    SafeErrorMessage: "Payment retry attempts exhausted.",
                    OccurredAtUtc: now,
                    CorrelationId: message.CorrelationId,
                    CausationId: message.EventId));

                inboxEntry!.ProcessedAtUtc = now;
                AttachInboxIfDetached(inboxEntry);
                await _dbContext.SaveChangesAsync(context.CancellationToken);
                return;
            }

            // Still leased / transient pending: DO NOT mark inbox completed!
            _logger.LogInformation("Payment for trip {TripId} is currently leased or pending. Retrying via broker.", message.TripId);
            throw new TransientPaymentPendingException($"Payment for trip {message.TripId} is currently leased or pending.");
        }
        else if (result.Payment.Status.Equals("settled", StringComparison.OrdinalIgnoreCase))
        {
            _eventWriter.Add(new PaymentSettledV1(
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
                CausationId: message.EventId));
        }
        else if (result.Payment.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            _eventWriter.Add(new PaymentFailedV1(
                EventId: Guid.NewGuid(),
                PaymentId: result.Payment.Id,
                TripId: message.TripId,
                PaymentKey: message.PaymentIdempotencyKey,
                FailureCode: result.Payment.FailureCode ?? "declined",
                IsPermanent: true,
                SafeErrorMessage: "The payment method was declined or failed.",
                OccurredAtUtc: now,
                CorrelationId: message.CorrelationId,
                CausationId: message.EventId));
        }

        inboxEntry!.ProcessedAtUtc = now;
        AttachInboxIfDetached(inboxEntry);
        await _dbContext.SaveChangesAsync(context.CancellationToken);
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
