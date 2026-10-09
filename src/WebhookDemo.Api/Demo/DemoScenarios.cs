using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookDemo.Data;
using WebhookDemo.Inbox;
using WebhookDemo.Webhooks;

namespace WebhookDemo.Demo;

/// <summary>
/// The dashboard's buttons. Each scenario signs its payload with the configured secret and hands it to the same
/// ingestion code that serves POST /webhooks/{source}, signature check included; only the network hop is skipped.
/// </summary>
public sealed class DemoScenarios(
    IServiceScopeFactory scopes,
    ScriptedChaosMonkey chaos,
    IOptions<WebhookOptions> webhooks,
    TimeProvider clock)
{
    public static readonly string[] Names =
    [
        "payment", "duplicate", "burst", "checkout",
        "crash-between-steps", "crash-before-commit", "crash-after-email",
        "flaky-provider", "poison", "whatsapp", "lead",
    ];

    public async Task<IReadOnlyList<IngestedEvent>?> RunAsync(string scenario, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        switch (scenario)
        {
            case "payment":
                return await SendAsync("stripe", DemoData.PaymentIntentSucceeded(now).Json, ct);

            case "duplicate":
            {
                // Stripe never saw our 200 (a timeout, a network blip), so it sends the same event again.
                var json = DemoData.PaymentIntentSucceeded(now).Json;
                var results = new List<IngestedEvent>();
                for (var i = 0; i < 3; i++) results.AddRange(await SendAsync("stripe", json, ct));
                return results;
            }

            case "burst":
            {
                // The same event five times at once: retries racing each other on different servers.
                var json = DemoData.PaymentIntentSucceeded(now).Json;
                var deliveries = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SendAsync("stripe", json, ct)));
                return deliveries.SelectMany(d => d).ToList();
            }

            case "checkout":
            {
                // Stripe Checkout announces one payment with two different events.
                var customer = DemoData.RandomCustomer();
                var (amount, currency) = (DemoData.RandomAmount(), DemoData.RandomCurrency());
                var session = DemoData.CheckoutSessionCompleted(now, DemoData.NewId("pi"), amount, currency, customer);
                var intent = DemoData.PaymentIntentSucceeded(now, session.PaymentIntentId, amount, currency, customer);
                return [.. await SendAsync("stripe", session.Json, ct), .. await SendAsync("stripe", intent.Json, ct)];
            }

            case "crash-between-steps":
                return await PaymentWithChaosAsync(ChaosPoint.AfterCheckpoint, "issue-invoice", ChaosAction.Crash, ct: ct);

            case "crash-before-commit":
                return await PaymentWithChaosAsync(ChaosPoint.AfterWork, "issue-invoice", ChaosAction.Crash, ct: ct);

            case "crash-after-email":
                return await PaymentWithChaosAsync(ChaosPoint.AfterWork, "send-receipt", ChaosAction.Crash, ct: ct);

            case "flaky-provider":
                return await PaymentWithChaosAsync(ChaosPoint.BeforeStep, "send-receipt", ChaosAction.TransientError, times: 2, ct);

            case "poison":
                return await PaymentWithChaosAsync(ChaosPoint.BeforeStep, "sync-crm", ChaosAction.PermanentError, ct: ct);

            case "whatsapp":
                return await SendAsync("whatsapp", DemoData.WhatsAppBatch(now), ct);

            case "lead":
                return await SendAsync("forms", DemoData.FormSubmission(now), ct);

            default:
                return null;
        }
    }

    /// <summary>Sends a stored event again, exactly as a provider retry would.</summary>
    public async Task<IReadOnlyList<IngestedEvent>?> RedeliverAsync(long inboxId, CancellationToken ct)
    {
        InboxEvent? evt;
        await using (var scope = scopes.CreateAsyncScope())
        {
            evt = await scope.ServiceProvider.GetRequiredService<WebhookDbContext>()
                .InboxEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == inboxId, ct);
        }
        if (evt is null) return null;

        var body = evt.Source == "whatsapp" ? DemoData.WrapWhatsAppMessage(evt.Payload) : evt.Payload;
        return await SendAsync(evt.Source, body, ct);
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        chaos.Clear();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WebhookDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Timeline.ExecuteDeleteAsync(ct);
        await db.Checkpoints.ExecuteDeleteAsync(ct);
        await db.InboxEvents.ExecuteDeleteAsync(ct);
        await db.Invoices.ExecuteDeleteAsync(ct);
        await db.Payments.ExecuteDeleteAsync(ct);
        await db.Leads.ExecuteDeleteAsync(ct);
        await db.ChatMessages.ExecuteDeleteAsync(ct);
        await db.ProviderCalls.ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<IReadOnlyList<IngestedEvent>> PaymentWithChaosAsync(
        ChaosPoint point, string step, ChaosAction action, int times = 1, CancellationToken ct = default)
    {
        var payment = DemoData.PaymentIntentSucceeded(clock.GetUtcNow());
        chaos.Add(new ChaosRule(point, step, action, times, externalId: payment.EventId));
        return await SendAsync("stripe", payment.Json, ct);
    }

    private async Task<IReadOnlyList<IngestedEvent>> SendAsync(string source, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var settings = webhooks.Value;
        var headers = new HeaderDictionary();
        switch (source)
        {
            case "stripe":
                var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
                var signature = Hmac.Sha256(settings.Stripe.SigningSecret, Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
                headers["Stripe-Signature"] = $"t={timestamp},v1={Convert.ToHexStringLower(signature)}";
                break;
            case "whatsapp":
                headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexStringLower(Hmac.Sha256(settings.WhatsApp.AppSecret, bytes));
                break;
            case "forms":
                headers["X-Signature-256"] = "sha256=" + Convert.ToHexStringLower(Hmac.Sha256(settings.Forms.SigningSecret, bytes));
                break;
        }

        await using var scope = scopes.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<WebhookIngestor>().IngestAsync(source, headers, bytes, ct);
        return outcome.Status == IngestStatus.Accepted
            ? outcome.Events
            : throw new InvalidOperationException($"The demo's own {source} delivery was rejected ({outcome.Status}: {outcome.Error}). Check the secrets in appsettings.");
    }
}
