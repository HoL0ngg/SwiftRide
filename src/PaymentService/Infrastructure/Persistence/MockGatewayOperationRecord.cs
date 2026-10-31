namespace SwiftRide.PaymentService.Infrastructure.Persistence;

public sealed class MockGatewayOperationRecord
{
    public Guid Id { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
