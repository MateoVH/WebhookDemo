namespace WebhookDemo.Inbox;

/// <summary>
/// Append-only history of an event. Correctness doesn't depend on it: it's the audit trail the dashboard shows.
/// </summary>
public sealed class TimelineEntry
{
    public long Id { get; set; }
    public long InboxEventId { get; set; }
    public DateTime At { get; set; }
    public TimelineKind Kind { get; set; }
    public string? Step { get; set; }
    public string? Detail { get; set; }
    public string? Worker { get; set; }
    public int? Attempt { get; set; }
}

public enum TimelineKind
{
    Received,
    Duplicate,
    Ignored,
    Claimed,
    LeaseExpired,
    Resumed,
    StepCompleted,
    StepFailed,
    RetryScheduled,
    Crashed,
    Released,
    Completed,
    DeadLettered,
    Replayed,
}
