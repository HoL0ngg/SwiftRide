using System.Text.Json;
using SwiftRide.Contracts.V1;
using SwiftRide.TripService.Application.Messaging;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Infrastructure.Messaging;

public sealed class TripEventWriter : ITripEventWriter
{
    private readonly TripDbContext _dbContext;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public TripEventWriter(TripDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(TripAcceptedV1 message)
    {
        StageEvent(message.EventId, nameof(TripAcceptedV1), message, message.OccurredAtUtc, message.CorrelationId, message.CausationId);
    }

    public void Add(RideCompletedV1 message)
    {
        StageEvent(message.EventId, nameof(RideCompletedV1), message, message.OccurredAtUtc, message.CorrelationId, message.CausationId);
    }

    public void Add(PaymentCompensationRequestedV1 message)
    {
        StageEvent(message.EventId, nameof(PaymentCompensationRequestedV1), message, message.OccurredAtUtc, message.CorrelationId, message.CausationId);
    }

    private void StageEvent<T>(Guid eventId, string eventType, T message, DateTimeOffset occurredAt, string correlationId, Guid? causationId)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var record = new IntegrationOutboxRecord
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
