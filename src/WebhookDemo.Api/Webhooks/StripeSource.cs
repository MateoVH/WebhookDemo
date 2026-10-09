using System.Text;
using Microsoft.Extensions.Options;
using WebhookDemo.Shared;

namespace WebhookDemo.Webhooks;

/// <summary>
/// Stripe. The Stripe-Signature header carries t=timestamp and v1=hex(HMAC-SHA256(secret, "{t}.{body}")); there can be
/// several v1 values while a secret is being rolled. Old timestamps are rejected, so a captured request can't be
/// replayed later. The event id (evt_…) is the deduplication key.
/// </summary>
public sealed class StripeSource(IOptions<WebhookOptions> options, TimeProvider clock) : IWebhookSource
{
    public string Name => "stripe";

    public SignatureCheck Verify(IHeaderDictionary headers, byte[] body)
    {
        var settings = options.Value.Stripe;
        if (string.IsNullOrEmpty(settings.SigningSecret)) return SignatureCheck.Invalid("Webhooks:Stripe:SigningSecret is not configured");

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in headers["Stripe-Signature"].ToString().Split(',', StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;

            var (key, value) = (part[..separator], part[(separator + 1)..]);
            if (key == "t" && long.TryParse(value, out var t)) timestamp = t;
            else if (key == "v1") signatures.Add(value);
        }
        if (timestamp is null || signatures.Count == 0) return SignatureCheck.Invalid("Missing or malformed Stripe-Signature header");

        var age = clock.GetUtcNow().ToUnixTimeSeconds() - (double)timestamp.Value;
        if (Math.Abs(age) > settings.Tolerance.TotalSeconds) return SignatureCheck.Invalid($"Timestamp is {age:0}s off, outside the tolerance (possible replay)");

        byte[] signedPayload = [.. Encoding.UTF8.GetBytes($"{timestamp}."), .. body];
        var expected = Hmac.Sha256(settings.SigningSecret, signedPayload);
        return signatures.Any(signature => Hmac.MatchesHex(expected, signature))
            ? SignatureCheck.Valid
            : SignatureCheck.Invalid("Signature mismatch");
    }

    public IReadOnlyList<IncomingEvent> Parse(byte[] body) => WebhookJson.Read<IReadOnlyList<IncomingEvent>>(body, root =>
    {
        var obj = root.GetProperty("data").GetProperty("object");
        var amount = obj.OptionalInt64("amount_received") ?? obj.OptionalInt64("amount_total") ?? obj.OptionalInt64("amount");
        var currency = obj.OptionalString("currency");
        var email = obj.OptionalString("receipt_email") ?? obj.OptionalObject("customer_details")?.OptionalString("email");

        var summary = new List<string>();
        if (amount is not null && currency is not null) summary.Add(Money.Format(amount.Value, currency));
        if (email is not null) summary.Add(email);

        return
        [
            new IncomingEvent(
                ExternalId: root.RequiredString("id"),
                EventType: root.RequiredString("type"),
                Payload: Encoding.UTF8.GetString(body),
                Summary: summary.Count > 0 ? string.Join(" · ", summary) : null),
        ];
    });
}
