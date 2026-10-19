namespace SwiftRide.TripService.Domain.Events;

public sealed record RideCompleted(
    Guid TripId,
    string RiderId,
    string DriverId,
    Guid QuoteId,
    decimal FareAmount,
    string Currency,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
