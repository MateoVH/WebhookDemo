namespace WebhookDemo.Pipelines.Payments;

public sealed class Invoice
{
    public long Id { get; set; }

    /// <summary>Consecutive, without gaps or repeats, as tax authorities require. Unique.</summary>
    public int Number { get; set; }

    /// <summary>One invoice per payment. Unique.</summary>
    public required string PaymentId { get; set; }

    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public string? CustomerEmail { get; set; }
    public DateTime IssuedAt { get; set; }

    public static string Code(int number) => $"INV-{number:D6}";
}
