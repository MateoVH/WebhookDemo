namespace WebhookDemo.Pipelines.WhatsApp;

public sealed class ChatMessage
{
    public long Id { get; set; }

    /// <summary>WhatsApp message id (wamid.…). Unique.</summary>
    public required string MessageId { get; set; }

    public required string From { get; set; }
    public string? ContactName { get; set; }
    public required string Type { get; set; }
    public string? Text { get; set; }
    public long InboxEventId { get; set; }
    public DateTime ReceivedAt { get; set; }
}
