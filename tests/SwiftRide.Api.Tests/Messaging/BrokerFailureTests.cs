using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.PaymentService.Domain.Exceptions;
using SwiftRide.PaymentService.Infrastructure.Messaging;
using Xunit;

namespace SwiftRide.Api.Tests.Messaging;

public class BrokerFailureTests
{
    public record TestMessage(Guid Id, string Payload);

    private class FlakyConsumer : IConsumer<TestMessage>
    {
        public static readonly ConcurrentDictionary<Guid, int> Attempts = new();
        public static int FailTimes = 1;

        public Task Consume(ConsumeContext<TestMessage> context)
        {
            var count = Attempts.AddOrUpdate(context.Message.Id, 1, (_, old) => old + 1);
            if (count <= FailTimes)
            {
                throw new InvalidOperationException($"Transient failure on attempt {count}");
            }

            return Task.CompletedTask;
        }
    }

    private class PermanentFailureConsumer : IConsumer<TestMessage>
    {
        public static readonly ConcurrentDictionary<Guid, int> Attempts = new();

        public Task Consume(ConsumeContext<TestMessage> context)
        {
            Attempts.AddOrUpdate(context.Message.Id, 1, (_, old) => old + 1);
            throw new ArgumentException("Permanent invalid argument error");
        }
    }

    private class DomainExceptionConsumer : IConsumer<TestMessage>
    {
        public static readonly ConcurrentDictionary<Guid, int> Attempts = new();

        public Task Consume(ConsumeContext<TestMessage> context)
        {
            Attempts.AddOrUpdate(context.Message.Id, 1, (_, old) => old + 1);
            throw new DomainException("Permanent domain business rule violation");
        }
    }

    private class AlwaysFailingConsumer : IConsumer<TestMessage>
    {
        public static readonly ConcurrentDictionary<Guid, int> Attempts = new();

        public Task Consume(ConsumeContext<TestMessage> context)
        {
            Attempts.AddOrUpdate(context.Message.Id, 1, (_, old) => old + 1);
            throw new TimeoutException("Database connection timeout");
        }
    }

    private static ServiceProvider BuildBusProvider<TConsumer>(int retryCount = 3) where TConsumer : class, IConsumer<TestMessage>
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMQ:FastRetryForTests"] = "true",
                ["RabbitMQ:RetryCount"] = retryCount.ToString()
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);

        services.AddMassTransit(x =>
        {
            x.AddConsumer<TConsumer>();

            x.UsingInMemory((context, cfg) =>
            {
                cfg.UseMessageRetry(r => BoundedMessageRetryPolicy.ConfigureRetry(r, config));
                cfg.ConfigureEndpoints(context);
            });
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task TransientFailure_IsRetriedAndSucceeds()
    {
        FlakyConsumer.Attempts.Clear();
        FlakyConsumer.FailTimes = 1;

        await using var provider = BuildBusProvider<FlakyConsumer>();
        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync();

        try
        {
            var messageId = Guid.NewGuid();
            await busControl.Publish(new TestMessage(messageId, "test-data"));

            // Wait for consumer to process with retries
            var completed = false;
            for (int i = 0; i < 20; i++)
            {
                if (FlakyConsumer.Attempts.TryGetValue(messageId, out var attempts) && attempts == 2)
                {
                    completed = true;
                    break;
                }
                await Task.Delay(50);
            }

            Assert.True(completed, "Message was not retried and completed as expected");
            Assert.Equal(2, FlakyConsumer.Attempts[messageId]);
        }
        finally
        {
            await busControl.StopAsync();
        }
    }

    [Fact]
    public async Task PermanentException_IsIgnoredByRetry_AndFailsImmediatelyWithoutRetry()
    {
        PermanentFailureConsumer.Attempts.Clear();

        await using var provider = BuildBusProvider<PermanentFailureConsumer>();
        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync();

        try
        {
            var messageId = Guid.NewGuid();
            await busControl.Publish(new TestMessage(messageId, "bad-argument"));

            // Wait brief moment to confirm it executed once and stopped
            await Task.Delay(300);

            Assert.True(PermanentFailureConsumer.Attempts.TryGetValue(messageId, out var count));
            Assert.Equal(1, count); // Exactly 1 attempt, 0 retries!
        }
        finally
        {
            await busControl.StopAsync();
        }
    }

    [Fact]
    public async Task DomainException_IsIgnoredByRetry_AndFailsImmediatelyWithoutRetry()
    {
        DomainExceptionConsumer.Attempts.Clear();

        await using var provider = BuildBusProvider<DomainExceptionConsumer>();
        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync();

        try
        {
            var messageId = Guid.NewGuid();
            await busControl.Publish(new TestMessage(messageId, "domain-conflict"));

            // Wait brief moment to confirm it executed once and stopped
            await Task.Delay(300);

            Assert.True(DomainExceptionConsumer.Attempts.TryGetValue(messageId, out var count));
            Assert.Equal(1, count); // Exactly 1 attempt, 0 retries!
        }
        finally
        {
            await busControl.StopAsync();
        }
    }

    [Fact]
    public async Task RetriesExhausted_AttemptsExactlyInitialPlusMaxRetries()
    {
        AlwaysFailingConsumer.Attempts.Clear();

        await using var provider = BuildBusProvider<AlwaysFailingConsumer>(retryCount: 3);
        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync();

        try
        {
            var messageId = Guid.NewGuid();
            await busControl.Publish(new TestMessage(messageId, "database-down"));

            // Wait for all retries (1 initial + 3 retries = 4 total attempts)
            var reached = false;
            for (int i = 0; i < 30; i++)
            {
                if (AlwaysFailingConsumer.Attempts.TryGetValue(messageId, out var attempts) && attempts >= 4)
                {
                    reached = true;
                    break;
                }
                await Task.Delay(50);
            }

            Assert.True(reached, "Retries did not exhaust to expected count");
            Assert.Equal(4, AlwaysFailingConsumer.Attempts[messageId]);
        }
        finally
        {
            await busControl.StopAsync();
        }
    }

    private class ReplayIdempotencyConsumer : IConsumer<TestMessage>
    {
        public static readonly ConcurrentDictionary<Guid, int> SideEffectExecutions = new();
        public static readonly ConcurrentDictionary<Guid, bool> ProcessedInbox = new();

        public Task Consume(ConsumeContext<TestMessage> context)
        {
            // Simulate Inbox check: if already processed, return immediately
            if (!ProcessedInbox.TryAdd(context.Message.Id, true))
            {
                // Duplicate / replayed message -> skip side effects
                return Task.CompletedTask;
            }

            // Execute side effect exactly once
            SideEffectExecutions.AddOrUpdate(context.Message.Id, 1, (_, old) => old + 1);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MessageReplay_DuplicateDelivery_IsDeduplicatedWithZeroAdditionalSideEffects()
    {
        ReplayIdempotencyConsumer.SideEffectExecutions.Clear();
        ReplayIdempotencyConsumer.ProcessedInbox.Clear();

        await using var provider = BuildBusProvider<ReplayIdempotencyConsumer>();
        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync();

        try
        {
            var messageId = Guid.NewGuid();

            // First delivery
            await busControl.Publish(new TestMessage(messageId, "financial-charge"));
            await Task.Delay(200);

            Assert.Equal(1, ReplayIdempotencyConsumer.SideEffectExecutions[messageId]);

            // Replay from operator (duplicate with same messageId)
            await busControl.Publish(new TestMessage(messageId, "financial-charge"));
            await Task.Delay(200);

            // Side effect count must remain strictly 1!
            Assert.Equal(1, ReplayIdempotencyConsumer.SideEffectExecutions[messageId]);
        }
        finally
        {
            await busControl.StopAsync();
        }
    }
}
