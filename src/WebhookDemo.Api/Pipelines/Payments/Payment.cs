namespace WebhookDemo.Pipelines.Payments;

public sealed class Payment
{
    public long Id { get; set; }

    /// <summary>Stripe PaymentIntent id (pi_…). Unique: recorded once even when several events talk about the same payment.</summary>
    public required string PaymentId { get; set; }

    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerName { get; set; }
    public long InboxEventId { get; set; }
    public DateTime RecordedAt { get; set; }
}
