namespace WebhookDemo.Inbox;

public sealed class ProcessingOptions
{
    public const string Section = "Processing";

    /// <summary>Worker loops in this process. 0 turns background processing off (the tests drive workers by hand).</summary>
    public int Workers { get; set; } = 2;

    /// <summary>How long a claimed event stays locked. If its worker dies, another one takes over after this.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>± this fraction of random jitter on retry delays, so a burst of failures doesn't retry in lockstep.</summary>
    public double RetryJitter { get; set; } = 0.2;
}
