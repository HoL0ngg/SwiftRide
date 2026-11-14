using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    private static ServiceProvider BuildProvider(DriverLocation[] drivers, Mock<IQuoteWriter> quoteWriter)
    {
        var reader = new Mock<IDriverLocationReader>();
        reader.Setup(r => r.ReadAvailableAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(drivers);

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(new Clock())
            .AddSingleton(new MatchingOptions())
            .AddSingleton<IPricingStrategy, StandardPricingStrategy>()
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

    [Fact]
    public async Task QuoteRequested_WithDriverAvailable_PublishesQuoteReady_AndStoresQuoteUnderRequestId()
    {
        var writer = new Mock<IQuoteWriter>();
        QuoteSnapshot? saved = null;
        writer.Setup(w => w.AddAsync(It.IsAny<QuoteSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<QuoteSnapshot, CancellationToken>((q, _) => saved = q).Returns(Task.CompletedTask);
        await using var provider = BuildProvider(OneDriverNearOrigin(), writer);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var request = QuoteRequest();
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
            new("drv-near", new GeoPoint(0, 0.01), true, Now),
            new("drv-far", new GeoPoint(0, 0.03), true, Now)
        ];
        await using var provider = BuildProvider(drivers, new Mock<IQuoteWriter>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var tripId = Guid.NewGuid();
        await harness.Bus.Publish(new DriverMatchRequestedV1(
            Guid.NewGuid(), tripId, 0, 0, ["drv-near"], Now, "corr-2"));

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
