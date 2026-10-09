namespace WebhookDemo.Inbox;

/// <summary>
/// Exponential backoff with jitter: 2s, 4s, 8s, 16s… up to a cap, give or take a little randomness
/// so that many events failing at once don't all come back at the same instant.
/// </summary>
public static class Backoff
{
    /// <param name="attempt">The attempt that just failed, starting at 1.</param>
    /// <param name="random">A random value in [0, 1).</param>
    public static TimeSpan Delay(int attempt, ProcessingOptions options, double random)
    {
        var exponential = options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(exponential, options.RetryMaxDelay.TotalMilliseconds);
        var jitter = 1 + options.RetryJitter * (2 * random - 1);
        return TimeSpan.FromMilliseconds(Math.Round(capped * jitter));
    }
}
