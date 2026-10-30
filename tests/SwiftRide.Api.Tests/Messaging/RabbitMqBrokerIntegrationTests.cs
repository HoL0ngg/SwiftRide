using System.Net.Sockets;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Infrastructure.Messaging;
using SwiftRide.TripService.Infrastructure.Messaging;
using Xunit;

namespace SwiftRide.Api.Tests.Messaging;

public class RabbitMqBrokerIntegrationTests
{
    private static bool IsRabbitMqAvailable(string host = "127.0.0.1", int port = 5672)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            if (connect.Wait(TimeSpan.FromSeconds(2)))
            {
                return client.Connected;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void TripService_RabbitMqConfigured_WithoutHost_FailsFast()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:Transport"] = "RabbitMQ",
                ["RabbitMQ:Host"] = "" // Missing!
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddTripMessaging(config);
        });

        Assert.Contains("RabbitMQ:Host is missing or empty", ex.Message);
    }

    [Fact]
    public void PaymentService_RabbitMqConfigured_WithoutHost_FailsFast()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:Transport"] = "RabbitMQ",
                ["RabbitMQ:Host"] = null // Missing!
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddPaymentMessaging(config);
        });

        Assert.Contains("RabbitMQ:Host is missing or empty", ex.Message);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("")]
    public void TripService_DefaultRestConfig_WithoutRabbitMqHost_StartsSuccessfully(string transport)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:Transport"] = transport,
                ["RabbitMQ:Host"] = "" // Empty default in REST mode
            })
            .Build();

        services.AddTripMessaging(config);
        using var provider = services.BuildServiceProvider();
        var writer = provider.GetService<SwiftRide.TripService.Application.Messaging.ITripEventWriter>();
        Assert.NotNull(writer);
        Assert.IsType<SwiftRide.TripService.Infrastructure.Messaging.NoOpTripEventWriter>(writer);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("")]
    public void PaymentService_DefaultRestConfig_WithoutRabbitMqHost_StartsSuccessfully(string transport)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:Transport"] = transport,
                ["RabbitMQ:Host"] = "" // Empty default in REST mode
            })
            .Build();

        services.AddPaymentMessaging(config);
        using var provider = services.BuildServiceProvider();
        var writer = provider.GetService<SwiftRide.PaymentService.Application.Messaging.IPaymentEventWriter>();
        Assert.NotNull(writer);
        Assert.IsType<SwiftRide.PaymentService.Infrastructure.Messaging.NoOpPaymentEventWriter>(writer);
    }

    [Fact]
    public async Task RealRabbitMq_PublishAndConsume_MessageDelivery_Verified()
    {
        if (!IsRabbitMqAvailable()) return;

        var tripReceivedTcs = new TaskCompletionSource<RideCompletedV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paymentReceivedTcs = new TaskCompletionSource<PaymentSettledV1>(TaskCreationOptions.RunContinuationsAsynchronously);

        var queueName = $"delivery-test-{Guid.NewGuid():N}";
        var busControl = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("127.0.0.1", 5672, "/", h =>
            {
                h.Username("guest");
                h.Password("guest");
            });

            cfg.ReceiveEndpoint(queueName, e =>
            {
                e.Durable = true;
                e.Handler<RideCompletedV1>(ctx =>
                {
                    tripReceivedTcs.TrySetResult(ctx.Message);
                    return Task.CompletedTask;
                });

                e.Handler<PaymentSettledV1>(ctx =>
                {
                    paymentReceivedTcs.TrySetResult(ctx.Message);
                    return Task.CompletedTask;
                });
            });
        });

        await busControl.StartAsync(TimeSpan.FromSeconds(15));
        try
        {
            var tripId = Guid.NewGuid();
            var eventId = Guid.NewGuid();

            var rideCompleted = new RideCompletedV1(
                EventId: eventId,
                TripId: tripId,
                RiderId: "rider-rmq-live",
                DriverId: "driver-rmq-live",
                QuoteId: Guid.NewGuid(),
                FareAmount: 50_000m,
                FareCurrency: "VND",
                PaymentMethod: "Card",
                MockTokenAlias: "tok_card_ok",
                PaymentIdempotencyKey: $"trip-{tripId:D}-payment",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"test-rmq-{tripId:D}");

            await busControl.Publish(rideCompleted);

            var receivedRide = await Task.WhenAny(tripReceivedTcs.Task, Task.Delay(5000));
            Assert.Same(tripReceivedTcs.Task, receivedRide);
            var receivedMsg = await tripReceivedTcs.Task;
            Assert.Equal(tripId, receivedMsg.TripId);
            Assert.Equal(eventId, receivedMsg.EventId);

            var paymentSettled = new PaymentSettledV1(
                EventId: Guid.NewGuid(),
                PaymentId: Guid.NewGuid(),
                TripId: tripId,
                RiderId: "rider-rmq-live",
                DriverId: "driver-rmq-live",
                Amount: 50_000m,
                Currency: "VND",
                PaymentKey: $"trip-{tripId:D}-payment",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"test-rmq-{tripId:D}");

            await busControl.Publish(paymentSettled);

            var receivedSettled = await Task.WhenAny(paymentReceivedTcs.Task, Task.Delay(5000));
            Assert.Same(paymentReceivedTcs.Task, receivedSettled);
            var settledMsg = await paymentReceivedTcs.Task;
            Assert.Equal(paymentSettled.PaymentId, settledMsg.PaymentId);
        }
        finally
        {
            await busControl.StopAsync();
        }
    }

    [Fact]
    public async Task RealRabbitMq_PoisonMessage_MovesTo_DeadLetterErrorQueue_Verified()
    {
        if (!IsRabbitMqAvailable()) return;

        var queueName = $"poison-test-{Guid.NewGuid():N}";
        var errorQueueName = $"{queueName}_error";
        var tripId = Guid.NewGuid();
        var failureReason = "Deliberate poison message for dead-letter error queue verification";

        // Step 1: Start consumer that fails on this message and exhausts retries (immediate 1 retry)
        var busFailing = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("127.0.0.1", 5672, "/", h =>
            {
                h.Username("guest");
                h.Password("guest");
            });

            cfg.ReceiveEndpoint(queueName, e =>
            {
                e.Durable = true;
                e.UseMessageRetry(r => r.Immediate(1));
                e.Handler<PaymentCompensationFailedV1>(_ =>
                {
                    throw new InvalidOperationException(failureReason);
                });
            });
        });

        await busFailing.StartAsync(TimeSpan.FromSeconds(15));
        try
        {
            var poisonMessage = new PaymentCompensationFailedV1(
                EventId: Guid.NewGuid(),
                RefundId: Guid.NewGuid(),
                PaymentId: Guid.NewGuid(),
                TripId: tripId,
                ReasonCode: "test-poison",
                SafeErrorMessage: "Poison message payload",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"corr-{tripId:N}");

            await busFailing.Publish(poisonMessage);

            // Allow message to fail twice and be moved by MassTransit into the _error queue
            await Task.Delay(2500);
        }
        finally
        {
            await busFailing.StopAsync();
        }

        // Step 2: Connect a dedicated reader directly to the dead-letter error queue ({queue}_error)
        var errorQueueReceivedTcs = new TaskCompletionSource<PaymentCompensationFailedV1>(TaskCreationOptions.RunContinuationsAsynchronously);

        var busErrorReader = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("127.0.0.1", 5672, "/", h =>
            {
                h.Username("guest");
                h.Password("guest");
            });

            cfg.ReceiveEndpoint(errorQueueName, e =>
            {
                e.Durable = true;
                e.Handler<PaymentCompensationFailedV1>(ctx =>
                {
                    errorQueueReceivedTcs.TrySetResult(ctx.Message);
                    return Task.CompletedTask;
                });
            });
        });

        await busErrorReader.StartAsync(TimeSpan.FromSeconds(15));
        try
        {
            var received = await Task.WhenAny(errorQueueReceivedTcs.Task, Task.Delay(8000));
            Assert.Same(errorQueueReceivedTcs.Task, received);

            var deadLetterMessage = await errorQueueReceivedTcs.Task;
            Assert.NotNull(deadLetterMessage);
            Assert.Equal(tripId, deadLetterMessage.TripId);
            Assert.Equal("test-poison", deadLetterMessage.ReasonCode);
        }
        finally
        {
            await busErrorReader.StopAsync();
        }
    }

    [Fact]
    public async Task RealRabbitMq_DurableQueue_MessageSurvives_ConsumerOffline_Verified()
    {
        if (!IsRabbitMqAvailable()) return;

        var queueName = $"durability-test-{Guid.NewGuid():N}";
        var tripId = Guid.NewGuid();

        // Step 1: Declare durable queue and exchange binding on RabbitMQ broker, then stop the consumer
        var setupBus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("127.0.0.1", 5672, "/", h =>
            {
                h.Username("guest");
                h.Password("guest");
            });

            cfg.ReceiveEndpoint(queueName, e =>
            {
                e.Durable = true;
                e.Handler<RideCompletedV1>(_ => Task.CompletedTask);
            });
        });

        await setupBus.StartAsync(TimeSpan.FromSeconds(15));
        await setupBus.StopAsync(); // Consumer is now completely offline!

        // Step 2: Publish message while consumer is offline
        var publisherBus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("127.0.0.1", 5672, "/", h =>
            {
                h.Username("guest");
                h.Password("guest");
            });
        });

        await publisherBus.StartAsync(TimeSpan.FromSeconds(15));
        try
        {
            var offlineMessage = new RideCompletedV1(
                EventId: Guid.NewGuid(),
                TripId: tripId,
                RiderId: "rider-durability",
                DriverId: "driver-durability",
                QuoteId: Guid.NewGuid(),
                FareAmount: 90_000m,
                FareCurrency: "VND",
                PaymentMethod: "Card",
                MockTokenAlias: "tok_card_ok",
                PaymentIdempotencyKey: $"trip-{tripId:D}-payment",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                CorrelationId: $"durability-{tripId:D}");

            await publisherBus.Publish(offlineMessage);
        }
        finally
        {
            await publisherBus.StopAsync();
        }

        // Wait to prove message resides stably inside RabbitMQ broker queue
        await Task.Delay(1000);

        // Step 3: Start a new consumer; it must pull the preserved message from the durable queue
        var consumerReceivedTcs = new TaskCompletionSource<RideCompletedV1>(TaskCreationOptions.RunContinuationsAsynchronously);

        var restartBus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("127.0.0.1", 5672, "/", h =>
            {
                h.Username("guest");
                h.Password("guest");
            });

            cfg.ReceiveEndpoint(queueName, e =>
            {
                e.Durable = true;
                e.Handler<RideCompletedV1>(ctx =>
                {
                    consumerReceivedTcs.TrySetResult(ctx.Message);
                    return Task.CompletedTask;
                });
            });
        });

        await restartBus.StartAsync(TimeSpan.FromSeconds(15));
        try
        {
            var received = await Task.WhenAny(consumerReceivedTcs.Task, Task.Delay(5000));
            Assert.Same(consumerReceivedTcs.Task, received);

            var messageFromBroker = await consumerReceivedTcs.Task;
            Assert.NotNull(messageFromBroker);
            Assert.Equal(tripId, messageFromBroker.TripId);
            Assert.Equal(90_000m, messageFromBroker.FareAmount);
        }
        finally
        {
            await restartBus.StopAsync();
        }
    }
}
