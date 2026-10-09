namespace WebhookDemo.Inbox;

/// <summary>
/// Durable proof that a step finished for an event. Unique per (event, step): a step can complete only once,
/// and every later attempt, by this worker or another one, skips the steps that already have a checkpoint.
/// </summary>
public sealed class StepCheckpoint
{
    public long Id { get; set; }
    public long InboxEventId { get; set; }
    public required string Step { get; set; }

    /// <summary>What the step produced (an invoice number, a message id…). Later steps can read it.</summary>
    public string? Result { get; set; }

    public required string Worker { get; set; }
    public int Attempt { get; set; }
    public DateTime CompletedAt { get; set; }
}
