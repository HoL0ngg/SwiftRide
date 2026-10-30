namespace SwiftRide.TripService.Application.Exceptions;

public sealed class ActiveTripConflictException(Guid activeTripId, string message) : Exception(message)
{
    public Guid ActiveTripId => activeTripId;
}

public sealed class IdempotencyConflictException(string message) : Exception(message);

public sealed class TripNotFoundException(Guid tripId) : Exception($"Trip '{tripId}' was not found.")
{
    public Guid TripId => tripId;
}

public sealed class UnauthorizedTripAccessException(string message) : Exception(message);

public sealed class InvalidTripCommandException(string message) : Exception(message);
