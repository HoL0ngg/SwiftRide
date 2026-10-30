using SwiftRide.TripService.Application.Reliability;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

public class RetryScheduleTests
{
    [Fact]
    public void NextDelay_Attempt1_ZeroJitter_ReturnsInitialDelay()
    {
        var delay = RetrySchedule.NextDelay(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), jitterFraction: 0.0);
        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void NextDelay_Attempt1_MaxJitter_Adds25Percent()
    {
        var delay = RetrySchedule.NextDelay(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), jitterFraction: 1.0);
        Assert.Equal(TimeSpan.FromSeconds(2.5), delay);
    }

    [Fact]
    public void NextDelay_Attempt2_ZeroJitter_DoublesInitialDelay()
    {
        var delay = RetrySchedule.NextDelay(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), jitterFraction: 0.0);
        Assert.Equal(TimeSpan.FromSeconds(4), delay);
    }

    [Fact]
    public void NextDelay_Attempt2_MaxJitter_Adds25PercentToDoubledDelay()
    {
        var delay = RetrySchedule.NextDelay(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), jitterFraction: 1.0);
        Assert.Equal(TimeSpan.FromSeconds(5.0), delay);
    }

    [Fact]
    public void NextDelay_HighAttempt_CapsAtMaxDelay()
    {
        var delayZeroJitter = RetrySchedule.NextDelay(10, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), jitterFraction: 0.0);
        Assert.Equal(TimeSpan.FromSeconds(60), delayZeroJitter);

        var delayMaxJitter = RetrySchedule.NextDelay(10, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), jitterFraction: 1.0);
        Assert.Equal(TimeSpan.FromSeconds(60), delayMaxJitter);
    }

    [Fact]
    public void NextDelay_RespectsRetryAfter_WhenLargerThanCalculatedDelay()
    {
        var delay = RetrySchedule.NextDelay(
            attempt: 1,
            initial: TimeSpan.FromSeconds(2),
            max: TimeSpan.FromSeconds(60),
            jitterFraction: 0.0,
            retryAfter: TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(15), delay);
    }

    [Fact]
    public void NextDelay_ClampsToDeadlineBoundary_WhenDelayExceedsRemainingTime()
    {
        var now = DateTimeOffset.UtcNow;
        var deadline = now.AddSeconds(3); // only 3 seconds remaining in budget

        var delay = RetrySchedule.NextDelay(
            attempt: 3, // calculated base = 8s
            initial: TimeSpan.FromSeconds(2),
            max: TimeSpan.FromSeconds(60),
            jitterFraction: 0.0,
            retryAfter: null,
            deadlineUtc: deadline,
            nowUtc: now);

        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }
}
