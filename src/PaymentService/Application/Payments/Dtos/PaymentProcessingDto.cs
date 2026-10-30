namespace SwiftRide.PaymentService.Application.Payments.Dtos;

public sealed record PaymentProcessingDto(
    int AttemptsStarted,
    DateTimeOffset? LeaseUntilUtc,
    bool CanRetry,
    bool RequiresAction);
