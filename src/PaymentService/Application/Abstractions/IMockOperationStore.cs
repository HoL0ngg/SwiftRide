namespace SwiftRide.PaymentService.Application.Abstractions;

public sealed record MockOperation(
    string Operation,
    string IdempotencyKey,
    string RequestHash,
    Func<CancellationToken, Task<GatewayResult>> Action);

public interface IMockOperationStore
{
    Task<GatewayResult> ExecuteOnceAsync(MockOperation input, CancellationToken ct);
}
