using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Enums;

namespace SwiftRide.PaymentService.Infrastructure.Gateways;

public abstract class MockPaymentGatewayBase(
    PaymentMethod method,
    string successToken,
    string declinedToken,
    string timeoutToken,
    IMockOperationStore? mockStore = null) : IPaymentGateway
{
    private readonly ConcurrentDictionary<string, GatewayResult> _inMemoryReceipts = new();

    public PaymentMethod Method => method;

    public async Task<GatewayResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Func<CancellationToken, Task<GatewayResult>> executeCharge = token =>
        {
            var isSuccess = request.PaymentToken == successToken
                || (method == PaymentMethod.Wallet && request.PaymentToken is "wallet.success.v1" or "wallet_ok")
                || (method == PaymentMethod.Card && request.PaymentToken is "card.success.v1" or "card_ok");

            var isDeclined = request.PaymentToken == declinedToken
                || (method == PaymentMethod.Wallet && request.PaymentToken is "wallet.declined.v1" or "wallet_declined")
                || (method == PaymentMethod.Card && request.PaymentToken is "card.declined.v1" or "card_declined");

            var isTimeout = request.PaymentToken == timeoutToken
                || (method == PaymentMethod.Wallet && request.PaymentToken is "wallet.timeout.v1" or "wallet_timeout")
                || (method == PaymentMethod.Card && request.PaymentToken is "card.timeout.v1" or "card_timeout");

            var result =
                isSuccess ? GatewayResult.Success($"mock-charge-{request.IdempotencyKey}")
                : isDeclined ? GatewayResult.Declined("payment_method_declined")
                : isTimeout ? GatewayResult.Transient("gateway_unavailable")
                : GatewayResult.Declined("unsupported_token");
            return Task.FromResult(result);
        };

        if (mockStore is not null)
        {
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.PaymentToken))).ToLowerInvariant();
            var op = new MockOperation("charge", request.IdempotencyKey, tokenHash, executeCharge);
            return await mockStore.ExecuteOnceAsync(op, ct);
        }

        var memResult = await executeCharge(ct);
        return Remember(request.IdempotencyKey, memResult);
    }

    public async Task<GatewayResult> RefundAsync(GatewayRefundRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Func<CancellationToken, Task<GatewayResult>> executeRefund = token =>
        {
            var result = GatewayResult.Success($"mock-refund-{request.IdempotencyKey}");
            return Task.FromResult(result);
        };

        if (mockStore is not null)
        {
            var refundPayload = $"{request.IdempotencyKey}:{request.OriginalReference}:{request.Amount.Amount:F0}:{request.Amount.Currency}";
            var refundHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refundPayload))).ToLowerInvariant();
            var op = new MockOperation("refund", request.IdempotencyKey, refundHash, executeRefund);
            return await mockStore.ExecuteOnceAsync(op, ct);
        }

        var memResult = await executeRefund(ct);
        return Remember(request.IdempotencyKey, memResult);
    }

    private GatewayResult Remember(string key, GatewayResult result) =>
        result.Outcome == GatewayOutcome.TransientFailure ? result : _inMemoryReceipts.GetOrAdd(key, result);
}
