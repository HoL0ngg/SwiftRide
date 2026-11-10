using System.Text.Json;
using SwiftRide.Contracts.V1;
using Xunit;

namespace SwiftRide.Api.Tests.Messaging;

public class ContractSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [Fact]
    public void TripAcceptedV1_RoundTripSerialization_PreservesAllFields()
    {
        var original = new TripAcceptedV1(
            EventId: Guid.NewGuid(),
            TripId: Guid.NewGuid(),
            RiderId: "rider-123",
            DriverId: "driver-456",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: "corr-" + Guid.NewGuid(),
            CausationId: Guid.NewGuid());

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TripAcceptedV1>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.EventId, deserialized.EventId);
        Assert.Equal(original.TripId, deserialized.TripId);
        Assert.Equal(original.RiderId, deserialized.RiderId);
        Assert.Equal(original.DriverId, deserialized.DriverId);
        Assert.Equal(original.OccurredAtUtc, deserialized.OccurredAtUtc);
        Assert.Equal(original.CorrelationId, deserialized.CorrelationId);
        Assert.Equal(original.CausationId, deserialized.CausationId);
    }

    [Fact]
    public void RideCompletedV1_RoundTripSerialization_PreservesFareCurrencyAndPaymentKey()
    {
        var tripId = Guid.NewGuid();
        var original = new RideCompletedV1(
            EventId: Guid.NewGuid(),
            TripId: tripId,
            RiderId: "rider-test",
            DriverId: "driver-test",
            QuoteId: Guid.NewGuid(),
            FareAmount: 75000m,
            FareCurrency: "VND",
            PaymentMethod: "wallet",
            MockTokenAlias: "mock-wallet-token-alias",
            PaymentIdempotencyKey: $"trip-{tripId}-payment",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: "corr-ride-completed-1",
            CausationId: Guid.NewGuid());

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<RideCompletedV1>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.EventId, deserialized.EventId);
        Assert.Equal(original.TripId, deserialized.TripId);
        Assert.Equal(original.FareAmount, deserialized.FareAmount);
        Assert.Equal("VND", deserialized.FareCurrency);
        Assert.Equal($"trip-{tripId}-payment", deserialized.PaymentIdempotencyKey);
        Assert.Equal(original.OccurredAtUtc, deserialized.OccurredAtUtc);
    }

    [Fact]
    public void PaymentSettledV1_RoundTripSerialization_PreservesBalancedLedgerAttributes()
    {
        var tripId = Guid.NewGuid();
        var original = new PaymentSettledV1(
            EventId: Guid.NewGuid(),
            PaymentId: Guid.NewGuid(),
            TripId: tripId,
            RiderId: "rider-settled",
            DriverId: "driver-settled",
            Amount: 120000m,
            Currency: "VND",
            PaymentKey: $"trip-{tripId}-payment",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: "corr-payment-settled",
            CausationId: Guid.NewGuid());

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PaymentSettledV1>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.PaymentId, deserialized.PaymentId);
        Assert.Equal(original.Amount, deserialized.Amount);
        Assert.Equal("VND", deserialized.Currency);
        Assert.Equal(original.PaymentKey, deserialized.PaymentKey);
    }

    [Fact]
    public void PaymentFailedV1_RoundTripSerialization_PreservesFailureClassification()
    {
        var tripId = Guid.NewGuid();
        var original = new PaymentFailedV1(
            EventId: Guid.NewGuid(),
            PaymentId: null,
            TripId: tripId,
            PaymentKey: $"trip-{tripId}-payment",
            FailureCode: "insufficient_funds",
            IsPermanent: true,
            SafeErrorMessage: "The wallet balance is insufficient.",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: "corr-payment-failed");

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PaymentFailedV1>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.PaymentId);
        Assert.Equal("insufficient_funds", deserialized.FailureCode);
        Assert.True(deserialized.IsPermanent);
        Assert.Null(deserialized.CausationId);
    }

    [Fact]
    public void CompensationEvents_RoundTripSerialization_PreservesAllAttributes()
    {
        var tripId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var req = new PaymentCompensationRequestedV1(
            EventId: Guid.NewGuid(),
            TripId: tripId,
            PaymentId: paymentId,
            PaymentKey: $"trip-{tripId}-payment",
            Amount: 50000m,
            Currency: "VND",
            ReasonCode: "trip_cancelled_after_charge",
            OccurredAtUtc: now,
            CorrelationId: "corr-comp-req");

        var refunded = new PaymentRefundedV1(
            EventId: Guid.NewGuid(),
            RefundId: refundId,
            PaymentId: paymentId,
            TripId: tripId,
            Amount: 50000m,
            Currency: "VND",
            OccurredAtUtc: now,
            CorrelationId: "corr-refunded");

        var failed = new PaymentCompensationFailedV1(
            EventId: Guid.NewGuid(),
            RefundId: null,
            PaymentId: paymentId,
            TripId: tripId,
            ReasonCode: "refund_gateway_unavailable",
            SafeErrorMessage: "Mock wallet provider temporarily unavailable",
            OccurredAtUtc: now,
            CorrelationId: "corr-comp-failed");

        var jsonReq = JsonSerializer.Serialize(req, JsonOptions);
        var dReq = JsonSerializer.Deserialize<PaymentCompensationRequestedV1>(jsonReq, JsonOptions);
        Assert.NotNull(dReq);
        Assert.Equal("trip_cancelled_after_charge", dReq.ReasonCode);
        Assert.Equal(50000m, dReq.Amount);

        var jsonRef = JsonSerializer.Serialize(refunded, JsonOptions);
        var dRef = JsonSerializer.Deserialize<PaymentRefundedV1>(jsonRef, JsonOptions);
        Assert.NotNull(dRef);
        Assert.Equal(refundId, dRef.RefundId);

        var jsonFail = JsonSerializer.Serialize(failed, JsonOptions);
        var dFail = JsonSerializer.Deserialize<PaymentCompensationFailedV1>(jsonFail, JsonOptions);
        Assert.NotNull(dFail);
        Assert.Equal("refund_gateway_unavailable", dFail.ReasonCode);
    }

    [Fact]
    public void EventTopology_Constants_MatchSpecifications()
    {
        Assert.Equal("SwiftRide.Contracts.V1:TripAcceptedV1", EventTopology.Exchanges.TripAccepted);
        Assert.Equal("SwiftRide.Contracts.V1:RideCompletedV1", EventTopology.Exchanges.RideCompleted);
        Assert.Equal("SwiftRide.Contracts.V1:PaymentSettledV1", EventTopology.Exchanges.PaymentSettled);
        Assert.Equal("SwiftRide.Contracts.V1:PaymentFailedV1", EventTopology.Exchanges.PaymentFailed);
        Assert.Equal("SwiftRide.Contracts.V1:PaymentCompensationRequestedV1", EventTopology.Exchanges.PaymentCompensationRequested);
        Assert.Equal("SwiftRide.Contracts.V1:PaymentRefundedV1", EventTopology.Exchanges.PaymentRefunded);
        Assert.Equal("SwiftRide.Contracts.V1:PaymentCompensationFailedV1", EventTopology.Exchanges.PaymentCompensationFailed);

        Assert.Equal("payment-ride-completed", EventTopology.Queues.PaymentRideCompleted);
        Assert.Equal("trip-payment-settled", EventTopology.Queues.TripPaymentSettled);
        Assert.Equal("trip-payment-failed", EventTopology.Queues.TripPaymentFailed);
        Assert.Equal("payment-compensation", EventTopology.Queues.PaymentCompensation);
        Assert.Equal("trip-payment-refunded", EventTopology.Queues.TripPaymentRefunded);
        Assert.Equal("trip-payment-compensation-failed", EventTopology.Queues.TripPaymentCompensationFailed);

        Assert.Equal("SwiftRide.Contracts.V1:QuoteRequestedV1", EventTopology.Exchanges.QuoteRequested);
        Assert.Equal("SwiftRide.Contracts.V1:QuoteReadyV1", EventTopology.Exchanges.QuoteReady);
        Assert.Equal("SwiftRide.Contracts.V1:QuoteFailedV1", EventTopology.Exchanges.QuoteFailed);
        Assert.Equal("SwiftRide.Contracts.V1:DriverMatchRequestedV1", EventTopology.Exchanges.DriverMatchRequested);
        Assert.Equal("SwiftRide.Contracts.V1:DriverMatchedV1", EventTopology.Exchanges.DriverMatched);
        Assert.Equal("SwiftRide.Contracts.V1:DriverMatchFailedV1", EventTopology.Exchanges.DriverMatchFailed);

        Assert.Equal("matching-quote-requested", EventTopology.Queues.MatchingQuoteRequested);
        Assert.Equal("matching-driver-match-requested", EventTopology.Queues.MatchingDriverMatchRequested);
        Assert.Equal("trip-quote-ready", EventTopology.Queues.TripQuoteReady);
        Assert.Equal("trip-quote-failed", EventTopology.Queues.TripQuoteFailed);
        Assert.Equal("trip-driver-matched", EventTopology.Queues.TripDriverMatched);
        Assert.Equal("trip-driver-match-failed", EventTopology.Queues.TripDriverMatchFailed);
    }

    [Fact]
    public void BootstrapTopology_RoutesMassTransitEvents_WhenConsumersHaveNotStarted()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SwiftRide.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var definitions = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "infra/rabbitmq/definitions.json")));
        var topology = definitions.RootElement;
        var routes = new[]
        {
            (typeof(RideCompletedV1), EventTopology.Queues.PaymentRideCompleted),
            (typeof(PaymentSettledV1), EventTopology.Queues.TripPaymentSettled),
            (typeof(PaymentFailedV1), EventTopology.Queues.TripPaymentFailed),
            (typeof(PaymentCompensationRequestedV1), EventTopology.Queues.PaymentCompensation),
            (typeof(PaymentRefundedV1), EventTopology.Queues.TripPaymentRefunded),
            (typeof(PaymentCompensationFailedV1), EventTopology.Queues.TripPaymentCompensationFailed),
            (typeof(QuoteRequestedV1), EventTopology.Queues.MatchingQuoteRequested),
            (typeof(DriverMatchRequestedV1), EventTopology.Queues.MatchingDriverMatchRequested),
            (typeof(QuoteReadyV1), EventTopology.Queues.TripQuoteReady),
            (typeof(QuoteFailedV1), EventTopology.Queues.TripQuoteFailed),
            (typeof(DriverMatchedV1), EventTopology.Queues.TripDriverMatched),
            (typeof(DriverMatchFailedV1), EventTopology.Queues.TripDriverMatchFailed)
        };

        foreach (var (messageType, queue) in routes)
        {
            var exchange = $"{messageType.Namespace}:{messageType.Name}";
            Assert.Contains(topology.GetProperty("exchanges").EnumerateArray(), e =>
                e.GetProperty("name").GetString() == exchange && e.GetProperty("durable").GetBoolean());
            Assert.Contains(topology.GetProperty("queues").EnumerateArray(), q =>
                q.GetProperty("name").GetString() == queue && q.GetProperty("durable").GetBoolean());
            Assert.Contains(topology.GetProperty("bindings").EnumerateArray(), b =>
                b.GetProperty("source").GetString() == exchange &&
                b.GetProperty("destination").GetString() == queue &&
                b.GetProperty("destination_type").GetString() == "queue");
        }
    }
}
