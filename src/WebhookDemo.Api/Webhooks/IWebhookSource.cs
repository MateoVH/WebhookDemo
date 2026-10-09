namespace WebhookDemo.Webhooks;

/// <summary>A webhook provider: how to tell a genuine delivery from a forged one, and how to split it into events.</summary>
public interface IWebhookSource
{
    /// <summary>The route segment: POST /webhooks/{Name}.</summary>
    string Name { get; }

    /// <summary>Checks the signature against the raw bytes exactly as received (re-serialized JSON wouldn't match).</summary>
    SignatureCheck Verify(IHeaderDictionary headers, byte[] body);

    /// <summary>
    /// Most providers send one event per request; WhatsApp batches several.
    /// Throws <see cref="MalformedWebhookException"/> when the body isn't what the provider documents.
    /// </summary>
    IReadOnlyList<IncomingEvent> Parse(byte[] body);
}

/// <param name="ExternalId">The provider's unique id for the event: the deduplication key.</param>
public sealed record IncomingEvent(string ExternalId, string EventType, string Payload, string? Summary);

public readonly record struct SignatureCheck(bool IsValid, string? Reason)
{
    public static SignatureCheck Valid => new(true, null);

    public static SignatureCheck Invalid(string reason) => new(false, reason);
}

public sealed class MalformedWebhookException(string message, Exception? inner = null) : Exception(message, inner);
