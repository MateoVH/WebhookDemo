namespace WebhookDemo.Pipelines.Leads;

public sealed class Lead
{
    public long Id { get; set; }

    /// <summary>The form tool's submission id. Unique.</summary>
    public required string SubmissionId { get; set; }

    public required string Email { get; set; }
    public string? Name { get; set; }
    public string? Company { get; set; }
    public string? Message { get; set; }
    public long InboxEventId { get; set; }
    public DateTime ReceivedAt { get; set; }
}
