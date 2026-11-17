using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwiftRide.Contracts.V1;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;
using SwiftRide.MatchingService.Infrastructure.Consumers;

namespace SwiftRide.MatchingService.Tests;

public class MatchingConsumerTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-28T00:00:00Z");
    }

    private static readonly DateTimeOffset Now = new Clock().GetUtcNow();

    private static ServiceProvider BuildProvider(DriverLocation[] drivers, Mock<IQuoteWriter> quoteWriter,
        Mock<ILogger<QuoteRequestedConsumer>>? logger = null)
    {
        var reader = new Mock<IDriverLocationReader>();
        reader.Setup(r => r.ReadAvailableAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(drivers);

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILogger<QuoteRequestedConsumer>>(logger?.Object ?? NullLogger<QuoteRequestedConsumer>.Instance)
            .AddSingleton<TimeProvider>(new Clock())
            .AddSingleton(new MatchingOptions())
            .AddSingleton<IPricingStrategy, StandardPricingStrategy>()
            .AddSingleton<IPricingStrategy>(new NightPricingStrategy(1.2m))
            .AddSingleton(reader.Object)
            .AddSingleton(quoteWriter.Object)
            .AddScoped<MatchDriver>()
            .AddScoped<CreateQuote>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<QuoteRequestedConsumer>();
                x.AddConsumer<DriverMatchRequestedConsumer>();
            })
            .BuildServiceProvider(true);
    }

    private static DriverLocation[] OneDriverNearOrigin() => [new("drv-1", new GeoPoint(0, 0.01), true, Now)];

    private static QuoteRequestedV1 QuoteRequest(Guid? eventId = null, string mode = "standard") => new(
        EventId: eventId ?? Guid.NewGuid(),
        TripId: Guid.NewGuid(),
        PickupLatitude: 0,
        PickupLongitude: 0,
        DestinationLatitude: 0,
        DestinationLongitude: 1,
        Mode: mode,
        OccurredAtUtc: Now,
        CorrelationId: "corr-1");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QuoteRequested_LogsReceivedAndPublished_WithLinkedEventIds(bool hasDriver)
    {
        var logger = new Mock<ILogger<QuoteRequestedConsumer>>();
        var writer = new Mock<IQuoteWriter>();
        writer.Setup(w => w.AddAsync(It.IsAny<QuoteSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await using var provider = BuildProvider(hasDriver ? OneDriverNearOrigin() : [], writer, logger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var request = QuoteRequest();

        await harness.Bus.Publish(request);
        Assert.True(await harness.GetConsumerHarness<QuoteRequestedConsumer>().Consumed.Any<QuoteRequestedV1>());
        var replyId = hasDriver
            ? harness.Published.Select<QuoteReadyV1>().Single().Context.Message.EventId
            : harness.Published.Select<QuoteFailedV1>().Single().Context.Message.EventId;
        var logs = logger.Invocations.Where(i => i.Method.Name == "Log")
            .Select(i => i.Arguments[2]!.ToString()!).ToArray();

        Assert.Contains(logs, line => line.Contains("[RECEIVED]") &&
            line.Contains(request.EventId.ToString()) && line.Contains(request.TripId.ToString()) &&
            line.Contains(request.CorrelationId));
        Assert.Contains(logs, line => line.Contains("[PUBLISHED]") &&
            line.Contains(replyId.ToString()) && line.Contains(request.EventId.ToString()) &&
            line.Contains(request.TripId.ToString()) && line.Contains(request.CorrelationId));
        var received = Assert.Single(logs, line => line.Contains("[RECEIVED]"));
        Assert.Contains("RABBITMQ MESSAGE RECEIVED", received);
        Assert.Contains("\n", received);
        Assert.Contains("[MESSAGE INFO]", received);
        Assert.Contains("[CORRELATION INFO]", received);
        Assert.Contains("[RETRY INFO]", received);
        Assert.Contains("[MESSAGE CONTEXT]", received);
        Assert.Contains("InputAddress", received);
        Assert.Contains("[CONTEXT.MESSAGE]", received);
        Assert.Contains("\"TripId\": \"" + request.TripId + "\"", received);
        Assert.Contains("\"Mode\": \"standard\"", received);
        Assert.DoesNotContain("PickupLatitude", received);
        var published = Assert.Single(logs, line => line.Contains("[PUBLISHED]"));
        Assert.Contains("RABBITMQ MESSAGE PUBLISHED", published);
        Assert.Contains("\n", published);
        Assert.Contains("[MESSAGE CONTEXT]", published);
    }

    [Theory]
    [InlineData("standard", "motorbike", 7500)]
    [InlineData("night", "motorbike", 9000)]
    [InlineData("standard", "car", 15000)]
    [InlineData("night", "car", 18000)]
    public async Task QuoteRequested_WithDriverAvailable_PublishesQuoteReady_AndStoresQuoteUnderRequestId(string mode, string vehicleType, decimal costPerKm)
    {
        var writer = new Mock<IQuoteWriter>();
        QuoteSnapshot? saved = null;
        writer.Setup(w => w.AddAsync(It.IsAny<QuoteSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<QuoteSnapshot, CancellationToken>((q, _) => saved = q).Returns(Task.CompletedTask);
        DriverLocation[] drivers = [new("wrong-type", new(0, 0), true, Now, vehicleType == "car" ? "motorbike" : "car"),
            new("drv-1", new(0, .01), true, Now, vehicleType)];
        await using var provider = BuildProvider(drivers, writer);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var request = QuoteRequest(mode: mode) with { VehicleType = vehicleType };
        await harness.Bus.Publish(request);

        Assert.True(await harness.Consumed.Any<QuoteRequestedV1>());
        Assert.True(await harness.Published.Any<QuoteReadyV1>());
        var reply = harness.Published.Select<QuoteReadyV1>().Single().Context.Message;
        Assert.Equal(request.TripId, reply.TripId);
        Assert.Equal("drv-1", reply.CandidateDriverId);
        Assert.Equal("VND", reply.Currency);
        Assert.Equal(request.CorrelationId, reply.CorrelationId);
        Assert.Equal(request.EventId, reply.CausationId);
        // Idempotency: the quote id is derived from the request, so a redelivery maps to the same quote.
        Assert.Equal(request.EventId, reply.QuoteId);
        Assert.Equal(request.EventId, saved!.Id);
        Assert.Equal(costPerKm, saved.Pricing.CostPerKm);
        Assert.Equal(vehicleType, saved.VehicleType);
        Assert.False(await harness.Published.Any<QuoteFailedV1>());
    }

    [Fact]
    public async Task QuoteRequested_WithNoDriver_PublishesQuoteFailed_AndWritesNoQuote()
    {
        var writer = new Mock<IQuoteWriter>(MockBehavior.Strict);
        await using var provider = BuildProvider([], writer);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(QuoteRequest());

        Assert.True(await harness.Published.Any<QuoteFailedV1>());
        Assert.Equal("NoDriverAvailable", harness.Published.Select<QuoteFailedV1>().Single().Context.Message.ReasonCode);
        Assert.False(await harness.Published.Any<QuoteReadyV1>());
    }

    [Fact]
    public async Task QuoteRequested_WithUnsupportedMode_PublishesQuoteFailedInvalidRequest_WithoutRetry()
    {
        var writer = new Mock<IQuoteWriter>(MockBehavior.Strict);
        await using var provider = BuildProvider(OneDriverNearOrigin(), writer);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(QuoteRequest(mode: "unknown"));

        Assert.True(await harness.Published.Any<QuoteFailedV1>());
        Assert.Equal("InvalidRequest", harness.Published.Select<QuoteFailedV1>().Single().Context.Message.ReasonCode);
    }

    [Fact]
    public async Task DriverMatchRequested_SkipsExcludedDrivers_AndEchoesExclusionList()
    {
        DriverLocation[] drivers =
        [
            new("bike-nearest", new GeoPoint(0, 0), true, Now),
            new("drv-near", new GeoPoint(0, 0.01), true, Now, "car"),
            new("drv-far", new GeoPoint(0, 0.03), true, Now, "car")
        ];
        await using var provider = BuildProvider(drivers, new Mock<IQuoteWriter>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var tripId = Guid.NewGuid();
        await harness.Bus.Publish(new DriverMatchRequestedV1(
            Guid.NewGuid(), tripId, 0, 0, ["drv-near"], Now, "corr-2", VehicleType: "car"));

        Assert.True(await harness.Published.Any<DriverMatchedV1>());
        var reply = harness.Published.Select<DriverMatchedV1>().Single().Context.Message;
        Assert.Equal(tripId, reply.TripId);
        Assert.Equal("drv-far", reply.DriverId);
        Assert.Equal(["drv-near"], reply.ExcludedDriverIds);
    }

    [Fact]
    public async Task DriverMatchRequested_WhenEveryDriverExcluded_PublishesDriverMatchFailed()
    {
        await using var provider = BuildProvider(OneDriverNearOrigin(), new Mock<IQuoteWriter>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new DriverMatchRequestedV1(
            Guid.NewGuid(), Guid.NewGuid(), 0, 0, ["drv-1"], Now, "corr-3"));

        Assert.True(await harness.Published.Any<DriverMatchFailedV1>());
        Assert.Equal("NoDriverAvailable", harness.Published.Select<DriverMatchFailedV1>().Single().Context.Message.ReasonCode);
        Assert.False(await harness.Published.Any<DriverMatchedV1>());
    }
}
