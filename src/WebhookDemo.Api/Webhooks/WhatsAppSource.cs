using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookDemo.Shared;

namespace WebhookDemo.Webhooks;

/// <summary>
/// WhatsApp Cloud API (Meta). X-Hub-Signature-256 is "sha256=" + hex(HMAC-SHA256(app secret, body)).
/// One delivery can batch several messages, and each message id (wamid.…) is deduplicated on its own.
/// </summary>
public sealed class WhatsAppSource(IOptions<WebhookOptions> options) : IWebhookSource
{
    public string Name => "whatsapp";

    public SignatureCheck Verify(IHeaderDictionary headers, byte[] body) =>
        Hmac.VerifyPrefixed(headers["X-Hub-Signature-256"].ToString(), options.Value.WhatsApp.AppSecret, body, "Webhooks:WhatsApp:AppSecret");

    public IReadOnlyList<IncomingEvent> Parse(byte[] body) => WebhookJson.Read<IReadOnlyList<IncomingEvent>>(body, root =>
    {
        var events = new List<IncomingEvent>();
        foreach (var entry in root.GetProperty("entry").EnumerateArray())
        foreach (var change in entry.GetProperty("changes").EnumerateArray())
        {
            // Delivery receipts (sent / delivered / read) arrive as "statuses"; this demo handles incoming messages.
            var value = change.GetProperty("value");
            if (change.OptionalString("field") != "messages" || value.OptionalArray("messages") is not { } messages) continue;

            var phoneNumberId = value.OptionalObject("metadata")?.OptionalString("phone_number_id");
            foreach (var message in messages.EnumerateArray())
            {
                var from = message.RequiredString("from");
                var type = message.RequiredString("type");
                var contactName = ContactName(value, from);
                var text = message.OptionalObject("text")?.OptionalString("body") ?? $"[{type}]";

                events.Add(new IncomingEvent(
                    ExternalId: message.RequiredString("id"),
                    EventType: $"message.{type}",
                    Payload: JsonSerializer.Serialize(new { phoneNumberId, contactName, message }),
                    Summary: $"{contactName ?? "+" + from}: {(text.Length > 60 ? text[..57] + "…" : text)}"));
            }
        }
        return events;
    });

    private static string? ContactName(JsonElement value, string waId)
    {
        if (value.OptionalArray("contacts") is not { } contacts) return null;

        foreach (var contact in contacts.EnumerateArray())
        {
            if (contact.OptionalString("wa_id") == waId) return contact.OptionalObject("profile")?.OptionalString("name");
        }
        return null;
    }
}
