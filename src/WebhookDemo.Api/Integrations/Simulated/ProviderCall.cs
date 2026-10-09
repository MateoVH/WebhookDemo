namespace WebhookDemo.Integrations.Simulated;

/// <summary>
/// A request as the simulated provider received it. It sits in our database only because this is a demo;
/// it is written in its own transaction, the way a call to another system would be.
/// </summary>
public sealed class ProviderCall
{
    public long Id { get; set; }

    /// <summary>email, crm or whatsapp.</summary>
    public required string Provider { get; set; }

    public required string Operation { get; set; }

    /// <summary>The idempotency key or the natural key the provider deduplicates on.</summary>
    public required string Key { get; set; }

    /// <summary>The id the provider returned (message id, deal id…).</summary>
    public required string ResourceId { get; set; }

    public required string Summary { get; set; }

    /// <summary>The provider recognized the key and returned the original result instead of acting again.</summary>
    public bool Deduplicated { get; set; }

    public DateTime At { get; set; }
}
