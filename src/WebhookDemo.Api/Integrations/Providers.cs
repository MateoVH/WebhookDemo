namespace WebhookDemo.Integrations;

/// <param name="Deduplicated">
/// The provider recognized the request (same idempotency key, or an upsert that matched an existing record)
/// and did not act a second time.
/// </param>
public sealed record ProviderResponse(string Id, bool Deduplicated);

/// <summary>Transactional email through an API that accepts idempotency keys: same key, the email goes out once.</summary>
public interface IEmailSender
{
    Task<ProviderResponse> SendAsync(string to, string subject, string body, string idempotencyKey, CancellationToken ct);
}

/// <summary>A HubSpot-style CRM. Records are upserted by a unique key, which makes these calls naturally idempotent.</summary>
public interface ICrmClient
{
    Task<ProviderResponse> UpsertDealAsync(string externalId, string name, long amountMinor, string currency, string? contactEmail, CancellationToken ct);

    Task<ProviderResponse> UpsertContactAsync(string key, string? name, string? email, string? phone, CancellationToken ct);
}

/// <summary>WhatsApp Cloud API. There is no idempotency key for outgoing messages: sending twice sends twice.</summary>
public interface IWhatsAppClient
{
    Task<ProviderResponse> SendTextAsync(string to, string text, CancellationToken ct);
}
