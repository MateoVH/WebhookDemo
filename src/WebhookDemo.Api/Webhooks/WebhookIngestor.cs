using WebhookDemo.Inbox;

namespace WebhookDemo.Webhooks;

public enum IngestStatus
{
    Accepted,
    UnknownSource,
    InvalidSignature,
    Malformed,
}

/// <param name="Status">accepted, duplicate or ignored (no pipeline for that event type).</param>
public sealed record IngestedEvent(string Id, string Type, string Status, int Deliveries, long InboxId);

public sealed record IngestOutcome(IngestStatus Status, IReadOnlyList<IngestedEvent> Events, string? Error = null);

/// <summary>
/// The receiving half: verify, split into events, store each one exactly once, answer right away.
/// Nothing is processed here. The workers do that, so a slow CRM can never make the provider time out and retry.
/// </summary>
public sealed class WebhookIngestor(
    IEnumerable<IWebhookSource> sources,
    IEnumerable<IWebhookPipeline> pipelines,
    InboxWriter inbox,
    ILogger<WebhookIngestor> logger)
{
    public async Task<IngestOutcome> IngestAsync(string sourceName, IHeaderDictionary headers, byte[] body, CancellationToken ct)
    {
        var source = sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        if (source is null) return new IngestOutcome(IngestStatus.UnknownSource, []);

        var signature = source.Verify(headers, body);
        if (!signature.IsValid)
        {
            logger.LogWarning("Rejected a {Source} webhook: {Reason}", source.Name, signature.Reason);
            return new IngestOutcome(IngestStatus.InvalidSignature, [], signature.Reason);
        }

        IReadOnlyList<IncomingEvent> events;
        try
        {
            events = source.Parse(body);
        }
        catch (MalformedWebhookException ex)
        {
            logger.LogWarning("Rejected a malformed {Source} webhook: {Reason}", source.Name, ex.Message);
            return new IngestOutcome(IngestStatus.Malformed, [], ex.Message);
        }

        var results = new List<IngestedEvent>(events.Count);
        foreach (var incoming in events)
        {
            var handled = pipelines.Any(p => p.CanHandle(source.Name, incoming.EventType));
            var stored = await inbox.RecordAsync(source.Name, incoming, handled, ct);
            var status = stored.IsDuplicate ? "duplicate" : handled ? "accepted" : "ignored";

            logger.LogInformation("{Source} {ExternalId} ({Type}): {Status}, delivery #{Delivery}",
                source.Name, incoming.ExternalId, incoming.EventType, status, stored.DeliveryCount);
            results.Add(new IngestedEvent(incoming.ExternalId, incoming.EventType, status, stored.DeliveryCount, stored.InboxId));
        }
        return new IngestOutcome(IngestStatus.Accepted, results);
    }
}
