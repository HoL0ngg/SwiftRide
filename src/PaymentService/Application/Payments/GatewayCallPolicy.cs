using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
namespace SwiftRide.PaymentService.Application.Payments;

public sealed class GatewayCallPolicy(PaymentGatewaySettings settings)   // 16.1 SRP: chỉ lo timeout cho lời gọi cổng
{
    public async Task<GatewayResult> RunAsync(Func<CancellationToken, Task<GatewayResult>> call, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);                            // 16.2 Mọi network call đều có timeout (AGENTS)
        try { return await call(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)  // 16.3 Hết giờ (không phải client hủy)
        { throw new PaymentGatewayUnavailableException("Payment gateway timed out."); }
    }
}