using System.Text.Json;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Application.Messaging;
using SwiftRide.PaymentService.Infrastructure.Persistence;

namespace SwiftRide.PaymentService.Infrastructure.Messaging;

public sealed class PaymentEventWriter : IPaymentEventWriter
{
    private readonly PaymentDbContext _dbContext;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public PaymentEventWriter(PaymentDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(PaymentSettledV1 message)
    {
        StageEvent(message.EventId, nameof(PaymentSettledV1), message, message.OccurredAtUtc, message.CorrelationId, message.CausationId);
    }

    public void Add(PaymentFailedV1 message)
    {
        StageEvent(message.EventId, nameof(PaymentFailedV1), message, message.OccurredAtUtc, message.CorrelationId, message.CausationId);
    }

    public void Add(PaymentRefundedV1 message)
    {
        StageEvent(message.EventId, nameof(PaymentRefundedV1), message, message.OccurredAtUtc, message.CorrelationId, message.CausationId);
    }

    public void Add(PaymentCompensationFailedV1 message)
    {
        StageEvent(message.EventId, nameof(PaymentCompensationFailedV1), message, message.OccurredAtUtc, message.CorrelationId, message.CausationId);
    }

    private void StageEvent<T>(Guid eventId, string eventType, T message, DateTimeOffset occurredAt, string correlationId, Guid? causationId)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var record = new PaymentOutboxRecord
        {
            MessageId = eventId,
            EventType = eventType,
            PayloadJson = json,
            CreatedAtUtc = occurredAt,
            NextAttemptUtc = occurredAt,
            Attempts = 0,
            MaxAttempts = 10,
            RequiresAction = false,
            CorrelationId = correlationId,
            CausationId = causationId,
            TraceParent = System.Diagnostics.Activity.Current?.Id
        };
        _dbContext.IntegrationOutbox.Add(record);
    }
}
