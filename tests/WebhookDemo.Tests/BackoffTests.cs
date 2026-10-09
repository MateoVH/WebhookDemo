using WebhookDemo.Inbox;

namespace WebhookDemo.Tests;

public sealed class BackoffTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(8, 256)]
    [InlineData(9, 300)] // capped at five minutes
    [InlineData(40, 300)]
    public void Delay_doubles_with_each_attempt_up_to_the_cap(int attempt, int expectedSeconds)
    {
        var options = new ProcessingOptions { RetryBaseDelay = TimeSpan.FromSeconds(2), RetryMaxDelay = TimeSpan.FromMinutes(5), RetryJitter = 0 };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Backoff.Delay(attempt, options, random: 0.5));
    }

    [Theory]
    [InlineData(0.0, 3.2)]
    [InlineData(0.5, 4.0)]
    [InlineData(0.99999, 4.8)]
    public void Jitter_spreads_retries_within_the_configured_ratio(double random, double expectedSeconds)
    {
        var options = new ProcessingOptions { RetryBaseDelay = TimeSpan.FromSeconds(2), RetryMaxDelay = TimeSpan.FromMinutes(5), RetryJitter = 0.2 };

        Assert.Equal(expectedSeconds, Backoff.Delay(2, options, random).TotalSeconds, precision: 2);
    }
}
