using Microsoft.EntityFrameworkCore;
using WebhookDemo.Data;
using WebhookDemo.Shared;

namespace WebhookDemo.Integrations.Simulated;

/// <summary>
/// Offline stand-ins for an email API, a HubSpot-style CRM and the WhatsApp Cloud API, faithful where it matters:
/// email honors idempotency keys, the CRM upserts by key, and WhatsApp sends every time it's asked.
/// Each call commits in its own scope and transaction, like a request to another system.
/// </summary>
public sealed class SimulatedProviders(IServiceScopeFactory scopes, TimeProvider clock) : IEmailSender, ICrmClient, IWhatsAppClient
{
    public Task<ProviderResponse> SendAsync(string to, string subject, string body, string idempotencyKey, CancellationToken ct) =>
        CallAsync("email", "send", idempotencyKey, $"{subject} → {to}", "msg", deduplicate: true, ct);

    public Task<ProviderResponse> UpsertDealAsync(
        string externalId, string name, long amountMinor, string currency, string? contactEmail, CancellationToken ct) =>
        CallAsync("crm", "upsert-deal", externalId, $"{name} · {Money.Format(amountMinor, currency)}", "deal", deduplicate: true, ct);

    public Task<ProviderResponse> UpsertContactAsync(string key, string? name, string? email, string? phone, CancellationToken ct) =>
        CallAsync("crm", "upsert-contact", key, name is null ? key : $"{name} ({key})", "contact", deduplicate: true, ct);

    public Task<ProviderResponse> SendTextAsync(string to, string text, CancellationToken ct) =>
        CallAsync("whatsapp", "send-text", to, $"→ +{to}: {text}", "wamid", deduplicate: false, ct);

    private async Task<ProviderResponse> CallAsync(
        string provider, string operation, string key, string summary, string idPrefix, bool deduplicate, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WebhookDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var original = deduplicate
            ? await db.ProviderCalls
                .Where(c => c.Provider == provider && c.Operation == operation && c.Key == key && !c.Deduplicated)
                .Select(c => c.ResourceId)
                .FirstOrDefaultAsync(ct)
            : null;

        var call = new ProviderCall
        {
            Provider = provider,
            Operation = operation,
            Key = key,
            ResourceId = original ?? $"{idPrefix}_{Guid.NewGuid().ToString("N")[..12]}",
            Summary = summary,
            Deduplicated = original is not null,
            At = clock.GetUtcNow().UtcDateTime,
        };
        db.ProviderCalls.Add(call);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ProviderResponse(call.ResourceId, call.Deduplicated);
    }
}
