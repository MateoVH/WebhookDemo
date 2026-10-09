namespace WebhookDemo.Inbox;

/// <summary>
/// A webhook event as received. It is stored once no matter how many times the provider delivers it:
/// (Source, ExternalId) is unique, and that constraint is what makes ingestion idempotent.
/// </summary>
public sealed class InboxEvent
{
    public long Id { get; set; }

    /// <summary>stripe, whatsapp or forms.</summary>
    public required string Source { get; set; }

    /// <summary>The provider's own id for the event (evt_…, wamid.…, submissionId): the deduplication key.</summary>
    public required string ExternalId { get; set; }

    public required string EventType { get; set; }
    public required string Payload { get; set; }

    /// <summary>One human-readable line for the dashboard ("49.00 EUR · ana@example.com").</summary>
    public string? Summary { get; set; }

    public InboxStatus Status { get; set; }

    /// <summary>Times the provider delivered this event: 1 + duplicates.</summary>
    public int DeliveryCount { get; set; }

    /// <summary>Times a worker started processing it. Counted when the event is claimed, so crashes count too.</summary>
    public int Attempts { get; set; }

    public DateTime ReceivedAt { get; set; }
    public DateTime LastDeliveredAt { get; set; }

    /// <summary>When the event is due: right away for new events, later for retries.</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>The lease: which worker owns the event, and until when. An expired lease means that worker died.</summary>
    public string? LockedBy { get; set; }

    public DateTime? LockedUntil { get; set; }

    public DateTime? CompletedAt { get; set; }
    public string? LastError { get; set; }
}

public enum InboxStatus
{
    /// <summary>Waiting for a worker: new, or scheduled for a retry.</summary>
    Pending,

    /// <summary>Leased by a worker.</summary>
    Processing,

    Completed,

    /// <summary>Acknowledged and kept for audit, but no pipeline handles this event type.</summary>
    Ignored,

    /// <summary>Gave up. A human can replay it once the cause is fixed.</summary>
    DeadLettered,
}
